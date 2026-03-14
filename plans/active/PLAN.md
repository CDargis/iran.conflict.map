# Iran Conflict Map — Refactor Plan

## Overview

Replacing the Wikipedia-based sync lambda with a CTP-ISW email-driven pipeline. The lambda will check an Outlook inbox daily, extract the CTP-ISW report URL from the email, scrape the report, and use Claude Haiku to extract structured strike events into DynamoDB.

---

## Schema Changes

`source_url` remains but `citations` is added. Full schema:

| Field | Type | Notes |
|---|---|---|
| `id` | String | Auto-incremented numeric string |
| `entity` | String | Always `"strike"` (GSI partition key) |
| `date` | String | ISO 8601, YYYY-MM-DD |
| `title` | String | Short event title |
| `location` | String | Human-readable place name |
| `lat` / `lng` | Number | Decimal degrees |
| `type` | String | `strike`, `drone`, `naval`, `missile` |
| `target_type` | String | `military`, `maritime`, `nuclear`, `command`, `civilian` |
| `actor` | String | Free text (e.g. US, Israel, Iran, Houthi) |
| `severity` | String | `low`, `medium`, `high`, `critical` |
| `description` | String | 1–3 sentence factual summary |
| `casualties` | Map | `{ confirmed: N, estimated: N }` |
| `source_url` | String (optional) | The CTP-ISW report URL — same for all events in a batch |
| `citations` | List of Strings (optional) | Inline footnote URLs resolved from the report's footnote block; only those tied to this event's paragraph |
| `disputed` | Boolean (optional) | Only set to `true` if contested/denied; omit otherwise |

### Severity Guidelines
- **low** — minor incident, 0 casualties, warning shots, disputed/intercepted attacks
- **medium** — limited engagement, 1–10 casualties, localized damage
- **high** — significant strike, 10–50 casualties, major infrastructure or military target
- **critical** — mass casualty event (50+ estimated), nuclear facility, decapitation strike, or major strategic escalation

---

## Two-Lambda Architecture

### Lambda 1: `IranConflictMap.Sync` — Email Checker (scheduled)
- Trigger: EventBridge scheduled rule, **9 PM daily**
- Responsibility: check Outlook, extract URL, fetch page, call Claude, push result to SQS
- Does NOT write to DynamoDB directly

### Lambda 2: `IranConflictMap.Lambda` — Processor (SQS-triggered)
- Trigger: SQS message
- Responsibility: read `{ new, updates, ambiguous }` off SQS and write to DynamoDB
- Used for both **live sync** (fed by `.Sync`) and **historical seeding** (manually enqueued)
- This is the piece built first

---

## Lambda 1: Sync Steps

1. **Check Outlook inbox** via Microsoft Graph API
   - Auth: personal Outlook account using OAuth (device code or client credentials with delegated permissions)
   - Emails are pre-filtered by an Outlook rule into a folder named **`queue`**
   - Read the first/latest email from that folder
   - After successful processing, move the email out of `queue` into a **`completed`** folder

2. **Extract report URL from email**
   - Use regex to find a `criticalthreats.org/analysis/` URL in the email body — no Claude call needed
   - Pattern: `https://www\.criticalthreats\.org/analysis/[^\s"<>]+`
   - If no URL is found: **fail the sync** — write a `no_url` error record to `syncs` table and stop; do not move the email

3. **Fetch report page**
   - HTTP GET the extracted URL
   - If fetch fails or returns empty content: **fail the sync** — write a `fetch_error` record to `syncs` table and stop; do not move the email
   - Note: criticalthreats.org may be JS-rendered — open question, must verify (see Open Questions)

4. **Call Claude Haiku with extraction prompt**
   - Pass full page text as user message; extraction prompt as system prompt
   - Response should be `{ new: [...], updates: [...], ambiguous: [...] }`
   - If Claude returns a non-200, malformed JSON, or a response missing all three arrays: **fail the sync** — write a `claude_error` record to `syncs` table and stop; do not move the email

5. **Push Claude response to SQS** — raw `{ new, updates, ambiguous }` JSON as the message body

6. **Move email** from `queue` folder to `completed` folder in Outlook — only on full success

7. **Write sync record** to `syncs` table (see schema below)

---

## Lambda 2: Processor Steps

SQS message body is raw Claude output: `{ "new": [...], "updates": [...], "ambiguous": [...] }`

1. **Process `new` array**
   - Get next available ID (query existing max + 1)
   - `BatchWriteItem` to `strikes` table (max 25 per batch)

2. **Process `updates` array**
   - For each update, look up the existing record:
     - If `lookup.id` is present → `GetItem` directly by ID
     - If no `id` → query entity GSI for `entity = "strike"`, filter by `date + location + actor`
       - 1 match → apply `UpdateItem` with only the changed fields
       - 0 matches → send to dead-letter SQS
       - 2+ matches → send to dead-letter SQS (ambiguous)
   - `citations` on updates: **union** incoming URLs with existing list, do not overwrite

3. **Process `ambiguous` array**
   - Send each item to dead-letter SQS
   - Do NOT write to `strikes` table

4. **Write sync record** to `syncs` table

---

## Syncs Table Schema (updated)

Replaces the old Wikipedia-era fields (`has_edits`, `last_synced`).

| Field | Type | Notes |
|---|---|---|
| `id` | String | ISO 8601 timestamp of the run (partition key) |
| `entity` | String | Always `"sync"` (GSI partition key) |
| `timestamp` | String | Same as `id` |
| `status` | String | `success`, `partial`, `no_email`, `no_url`, `fetch_error`, `claude_error`, `error` |
| `report_url` | String (optional) | The CTP-ISW URL that was processed |
| `new_event_count` | Number | Events written to `strikes` |
| `update_count` | Number | Updates applied to existing records |
| `dead_letter_count` | Number | Items sent to SQS dead-letter (failed updates + ambiguous) |
| `error_message` | String (optional) | Human-readable detail on failures (e.g. "3 updates dead-lettered; 1 ambiguous items dead-lettered") |

- **`partial`** status means the run succeeded but some items were dead-lettered — visible in the UI so you know to check the DLQ

---

## Claude API Call

- **Model:** `claude-haiku-4-5-20251001`
- **System prompt:** the extraction prompt (see `prompt.txt` for full text)
- **User message:** the full CTP-ISW page text
- **Expected response:** a single JSON object with three arrays: `new`, `updates`, `ambiguous`

The prompt instructs Claude to:
- Extract one event per distinct operation/topline paragraph (no individual munitions)
- Classify each as `new`, `update`, or `ambiguous`
- Resolve inline footnote markers (e.g. `[i]`, `[xv]`) to full URLs from the report's footnote block
- Set `source_url` to the report URL (same for all events in a run)
- Include `next_id` and `last_synced` date in the user message so Claude knows where to start IDs and what date range has already been logged

See `prompt.txt` for the full prompt text including DynamoDB wire format examples.

### Edge Cases

- **URL date format** — CTP-ISW URLs use no zero-padding (e.g. `march-7` not `march-07`)
- **Report density** — morning reports ~30 footnotes, evening reports ~100–170; both must be handled
- **Update `id` presence** — seed files include `id` in the lookup (Claude remembered IDs from the same session); live sync updates will not have `id` since Claude has no DB knowledge
- **Update with multiple matches** — if `date + location + actor` matches more than one existing record, do NOT write; send to dead-letter SQS
- **Citations on updates** — union incoming URLs with existing list; do not overwrite
- **Misclassification** — Haiku may misclassify new events as updates or vice versa; `ambiguous` array is the safety valve
- **JS-rendered page** — criticalthreats.org may be JS-rendered; must verify before coding `.Sync` (open question #4)

---

## Historical Data

- Historical events will be seeded manually
- Workflow: use Claude (via claude.ai or API) with `prompt.txt` to process past CTP-ISW reports
- Output is raw DynamoDB JSON (`PutRequest` format) that can be fed directly into the AWS CLI:
  ```bash
  aws dynamodb batch-write-item --request-items file://batch.json
  ```
- Schema includes `source_url` pointing to the specific CTP-ISW report for each batch

---

## TODOs

- [ ] **DLQ alerting** — set up notifications when messages land in the dead-letter queue (CloudWatch alarm on `ApproximateNumberOfMessagesVisible > 0` → SNS → email, or similar). Decide on mechanism.

## Open Questions

1. ~~**Outlook auth**~~ — personal Outlook, Microsoft Graph API with OAuth. ✅
2. **Email identification** — sender address and/or subject pattern for CTP-ISW emails. User will set up an Outlook rule to route them to the `queue` folder, but exact rule criteria TBD (need to see a real email).
3. ~~**Ambiguous dead-letter**~~ — SQS queue. ✅
4. **criticalthreats.org rendering** — is full report text in raw HTML or JS-rendered? Must verify before coding the scrape step. Test with a `curl` against a known report URL before implementing.

---

## Files to Change

| File | Change |
|---|---|
| `src/IranConflictMap.Lambda/Function.cs` | **Build first** — SQS-triggered processor: handles `new`, `updates`, `ambiguous` arrays, writes to DynamoDB |
| `src/IranConflictMap.Sync/Function.cs` | **Build second** — full rewrite: Outlook → regex → page fetch → Claude → SQS |
| `src/IranConflictMap/IranConflictMapStack.cs` | Update CDK: EventBridge 9 PM schedule, SQS queue + dead-letter queue, SSM params for Graph API OAuth creds |
| `prompt.txt` | Already updated ✅ |
| DynamoDB `strikes` table | Add `citations` (L), keep `source_url` (S), wipe existing data |
| DynamoDB `syncs` table | Drop `has_edits`/`last_synced`, add `report_url`, `update_count`, `ambiguous_count`, `error_message` |

---

## Current State

- `prompt.txt` — updated with new prompt and schema
- `src/IranConflictMap.Sync/Function.cs` — still has old Wikipedia-based logic; not yet updated
- `bin/` and `obj/` build artifacts — untracked from git (done)
- `.claude/settings.local.json` — added to `.gitignore` (done)
