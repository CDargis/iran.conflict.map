# Morning Reports — Planning Document

## Background

CTP-ISW publishes two report types per day:
- **Morning Special Report** (~08:00 AM ET) — early snapshot, 5–7 events, military ops only, light casualties
- **Evening Report / Regular Daily** — comprehensive 24-hour picture, 10+ sections, supersedes morning

Morning reports are a proper subset of the day's events. The evening report covers the same ground with more detail, plus casualties, diplomacy, and leadership content.

## Key Problems to Solve

### 1. Source / URL Discovery
Morning reports do not come via the CTP-ISW email subscription (TBD — still investigating).
Will likely need a polling/discovery mechanism similar to the existing 3-strategy URL resolution:
- Subject slug pattern: `iran-update-morning-special-report-{month}-{day}-{year}`
- INI_LIST lookup (listing page)
- Body scan fallback
No implementation yet — URL source is still being researched.

### 2. New vs Update vs Delete
The evening report relationship to the morning report is not strictly additive:
- Most morning events will appear in the evening with more detail → **update**
- Some morning events may be walked back entirely → **delete**
- Evening may have net-new events not in the morning → **new**
- Rarely: a morning event may have no evening counterpart at all → **keep as-is**

Claude's existing proximity-based new/update classification may struggle here because the
evening report doesn't know about morning events — it just describes the day's events fresh.

### 3. Preliminary Flag Strategy
Add a `preliminary: true` boolean to events extracted from morning reports.
Use a DynamoDB sparse GSI on this field so we can efficiently query all preliminary events.

When the evening report runs:
- All `preliminary = true` events for that date need to be resolved
- Options per event:
  a. Matched by proximity → flip `preliminary` to false, apply updates
  b. Walked back / not mentioned → delete
  c. No match found → keep but leave as preliminary (manual review?)

### 4. Evening Processing Approach (two options under consideration)

**Option A — Smart merge:**
- Claude processes the evening report normally (new/update/ambiguous)
- Post-processing step: query all `preliminary = true` events for the date
- Any that weren't touched by the evening sync get deleted or flagged
- Requires knowing which events the evening report "covered"

**Option B — Nuke and replace:**
- When evening report runs for a given date, delete all `preliminary = true` events for that date first
- Then process the evening report as entirely new data
- Simpler logic, no matching needed
- Downside: brief gap between delete and write; loses any morning-only events that didn't make it into the evening

**Current lean:** Option B (nuke and replace) is simpler and avoids the matching problem entirely.
The evening report is authoritative — anything in the morning that the evening doesn't mention
probably shouldn't be in the DB anyway.

## Schema Changes Required

### New field: `preliminary`
- Type: Boolean
- Present only on events extracted from morning reports
- Omitted (not false) on evening/regular events — sparse index will only include morning events
- DynamoDB sparse GSI: partition key `preliminary` (only indexes items where field exists)

### New field: `source`
- Type: String — values: `"morning"` | omitted
- Set at write time when an event originates from a morning report
- **Permanent** — never changed or removed, even after the evening report confirms or supersedes the event
- Absent on events that were never published in a morning report (evening-only events)
- Purpose: audit trail — lets you query "what was first reported in the morning" even months later
- Distinct from `preliminary`: `preliminary` is a lifecycle flag (cleared on resolution); `source` is an origin label (immutable)

### New GSI on strikes table
- Name: `preliminary-date-index` (or similar)
- Partition key: `preliminary` (Boolean)
- Sort key: `date` (String)
- Allows querying: "all preliminary events on date X"

### System prompt change
- Claude needs to know whether it's processing a morning or evening report
- If morning: set `preliminary: true` and `source: "morning"` on all new events
- If evening: do NOT set preliminary (it's the authoritative source); do NOT set source

## Audit Trail Design

### Approach: dedicated audit rows (single-table design)
Event items already have an `update_log` field that tracks field changes inline. This covers
the simple case but doesn't support cross-event queries (e.g. "show me everything that changed
on a given date"). Dedicated audit rows solve that.

### Audit row schema
| Field | Type | Description |
|---|---|---|
| `entity` | String (PK) | `"audit"` — fixed value, identifies row type |
| `id` | String (SK) | `"{event_id}#{timestamp_iso}"` — unique per change |
| `event_id` | String | FK to the event that changed |
| `at` | String (ISO 8601) | When the change occurred |
| `source` | String | What caused the change: `"morning"`, `"evening"`, `"review"`, `"manual"` |
| `fields` | String Set | Which fields changed (e.g. `["casualties", "description"]`) |
| `values_before` | Map | Field values before the change (omitted on insert) |
| `values_after` | Map | Field values after the change |

### When audit rows are written
- **New event created**: audit row with `source = trigger source`, no `values_before`
- **Evening updates a morning event**: audit row with `source = "evening"`, before/after for changed fields
- **Review queue resolution**: audit row with `source = "review"`
- **Preliminary event deleted** (nuke-and-replace): audit row with `source = "evening"`, `fields = ["*"]`, noting deletion

### Relationship to `update_log`
`update_log` on the event item is a convenience field for display (last N changes inline).
Audit rows are the authoritative, queryable history. Both can coexist — write both on updates.
If that's too much overhead initially, start with audit rows only and drop `update_log`.

## Processing Flow (proposed)

### Morning sync:
1. Discover URL (TBD mechanism)
2. Extract → Sync Lambda → Claude (with morning flag in prompt)
3. Claude outputs events with `preliminary: true`
4. Processor writes them normally — they appear on the map with some visual indicator

### Evening sync (same date):
1. Existing email-triggered flow
2. **Before** calling Claude: query `preliminary-date-index` for today's date, collect IDs
3. Claude processes evening report (no preliminary flag)
4. Processor writes evening events
5. Post-write: delete all previously collected preliminary IDs that weren't updated

## Open Questions

- How are morning reports discovered / triggered? Email? Polling? Manual?
- Should preliminary events show differently on the map (dimmed, different marker)?
- What's the cutoff — if no evening report arrives by e.g. 10 PM, keep preliminary events?
- Should "walked back" events go to the review queue rather than being auto-deleted?
- Do we want a `source: "morning"` field for audit purposes even after preliminary is resolved?

## Impact Assessment — Changes from 2026-03-18

### "All matched updates go to review" interaction

Today's change routes every matched update through the review queue. This has a significant
effect on Option A (smart merge):

- When the evening report matches a morning preliminary event, that update goes to review
- With potentially many morning events, this could mean dozens of manual approvals for what
  should be an automatic morning→evening resolution
- **Option A becomes significantly more complex** in this architecture unless a bypass is added
  for preliminary→confirmed transitions (e.g. `is_preliminary_resolution` flag)

**Option B (nuke-and-replace) is unaffected and now even more attractive:**
- Delete step runs before Claude processes the evening report — no review queue involved
- Evening events are all classified as new (no existing records to match against)
- New events bypass the review queue entirely
- The complication simply doesn't exist

### Other changes — all compatible

| Change | Impact |
|---|---|
| `run_id` stamped at enqueue | Compatible — morning syncs get stable run IDs |
| URL normalization at enqueue | Beneficial — morning URLs normalized same as evening |
| `notes` additive / `description` banned | No overlap with morning report fields |
| `syncs-v2` (report_url PK + run_id SK) | Compatible — morning/evening URLs are distinct, separate partitions |
| `source_url` consolidation on sync records | No conflict — plan's `source` field is on the strikes table, different concept |

### Naming note
The plan defines a `source` field on strike events (`"morning"` | omitted). This is unrelated
to `source_url` on sync records. No collision, but worth keeping in mind during implementation
to avoid confusion across the two tables.

## Open Questions

### Economic Signal Rows — Multiple Syncs Per Day
Once morning reports are live, a single date may produce two `economic-signals` rows — one from the morning sync, one from the evening. The composite key (`date` PK + `source_url` SK) handles this naturally since morning and evening URLs are distinct. How the API surfaces them is already decided (latest wins, per 9B in `ECONOMIC_DATA_PLAN.md`), but confirm this is the right behavior when both a morning and evening extraction exist for the same day — a merged union of `economic_notes` may be preferable to dropping the morning extraction entirely.

## Not Building Yet
URL source is unresolved. Nothing in this plan should be implemented until the discovery
mechanism is figured out. This document is for architectural alignment only.
