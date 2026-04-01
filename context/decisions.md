# Iran Conflict Map — Decisions

Lightweight ADR format. Decisions inferred from code, comments, and plans/.

---

## Claude Sonnet 4.6 (not Haiku)

**Decision**: Use Claude Sonnet 4.6 for extraction.

**Why**: Better extraction quality. Haiku was initially used but produced misclassification
errors (wrong event types, missed updates). Switched to Sonnet for reliability.

---

## All matched updates go to review queue

**Decision**: Processor Lambda routes every proximity-matched update to the review queue rather
than applying it directly.

**Why**: Human judgment on whether an AI-matched "update" is actually the same event. Prevents
silent data corruption from false-positive proximity matches.

**Consequence**: Review approval loop required for all updates. This was added 2026-03-18 and
was explicitly noted as making Option A (smart merge) for morning reports significantly more
complex. Strengthens the case for Option B (nuke-and-replace) for morning reports.

---

## Review approval bypasses proximity threshold

**Decision**: When `is_review_approval=true` on a SyncEnvelope, Processor applies the update to
the nearest match unconditionally (no 10km threshold).

**Why**: A human has already reviewed the match. Enforcing the threshold again would require
the reviewer to manually correct coordinates before approving, which is impractical.

---

## GUIDs stamped at Sync Lambda (not Processor)

**Decision**: New events get their `id` GUID stamped by Sync Lambda before enqueue, not by
Processor Lambda at write time.

**Why**: Allows the review queue to include the stable `id` in review messages, so that an
approved "new" event from review gets the same ID it would have gotten automatically. Also
enables idempotent re-queuing.

---

## FIFO SQS queues throughout

**Decision**: All 4 queues are FIFO with content-based deduplication.

**Why**: Ordering matters (Extract → Sync → Processor chain), and deduplication prevents
double-processing if a Lambda retries or a message is re-enqueued with the same `run_id`.

---

## Proximity matching for update lookup (Haversine, 10km, ±1 day)

**Decision**: When an update from Claude lacks an `id`, find the matching event by querying the
entity-date GSI (date ±1 day window) and ranking candidates by Haversine distance. Accept only
if closest candidate is ≤10km.

**Why**: Claude doesn't always have the original event ID. Location + date is a practical
surrogate key for strike events. The 10km threshold filters false positives; the ±1 day window
handles date ambiguity in reports.

---

## `description` is immutable; updates use `notes`

**Decision**: The `description` field on a strike event is set once at creation and never
overwritten. Subsequent reports that add details append to the `notes` list instead.

**Why**: Preserves the original extraction verbatim for audit purposes. New details from later
reports are additive, not replacements.

---

## `citations` field is a union (not replace)

**Decision**: When an update adds `citations`, Processor merges them with existing citations
rather than replacing.

**Why**: Multiple reports may cite overlapping but non-identical source sets. Replacing would
lose citations from earlier reports.

---

## Nuke-and-replace for morning reports (chosen, not yet implemented)

**Decision**: When the evening report runs for a given date, delete all `preliminary=true`
events for that date first, then process the evening report as entirely new data.

**Why**: Simpler than smart-merge. The evening report is authoritative — morning events that
don't appear in the evening report shouldn't be in the DB. Also, with the "all updates go to
review" rule in place, Option A (smart merge) would flood the review queue with preliminary→
confirmed transitions, requiring excessive manual approvals.

---

## `syncs` table retained as legacy (no longer written)

**Decision**: The original `syncs` table is kept in the CDK stack but nothing writes to it.
`syncs-v2` replaced it with a composite PK (report_url + run_id) for better idempotency.

**Why**: Avoiding a destructive removal during a live refactor. Can be cleaned up later.

---

## Vanilla JS frontend (no framework)

**Decision**: index.html is a single compiled file with no build step.

**Why**: No build pipeline needed; direct S3 deployment; Leaflet.js is battle-tested for maps.
The scope of the UI is narrow enough that a framework would add complexity without benefit.

---

## Minimal API (.NET) over controllers

**Decision**: All API endpoints defined inline in Program.cs using minimal API pattern.

**Why**: Less boilerplate. All endpoint logic is co-located and readable at a glance. The API
surface is small and stable.

---

## GSI on signals table for sticky Hormuz (not scan)

**Decision**: Added `entity-date-index` GSI (PK: `entity`, SK: `date`) to the signals table.
All rows carry `entity = "signal"`. API queries GSI backwards to find last known Hormuz status.

**Why**: Loading all signals upfront via scan was rejected — `economic_notes` fields contain
substantial text and the scan would grow unbounded. Per-date fetch + GSI fallback reads only
what's needed. The `entity` trick (all rows share one PK value) turns the GSI into a
date-sorted collection that supports range queries.

---

## `no_alert` means silent; `open` means explicitly confirmed

**Decision**: `hormuz_status = "no_alert"` means the report did not mention Hormuz (silent).
`"open"` is a separate value used only when the report explicitly confirms normal passage.

**Why**: If `no_alert` meant "open," there would be no way to distinguish silence from a
confirmed open status. The API treats `no_alert` as "unknown — use sticky" and only surfaces
a real status when a prior row explicitly says `restricted`, `closed`, or `open`.

---

## Signals fetched per date, not upfront

**Decision**: Frontend fetches `/api/economic/signals?date=YYYY-MM-DD` on each date
navigation, rather than loading all signals at page start.

**Why**: Text in `economic_notes` is large. Loading all rows upfront would mean a scan
returning unbounded data. Per-date fetch is cheap (single PK query) and scales without limit.

---

## Deploy date injected at CDK synth time

**Decision**: CDK injects the current date into the frontend HTML as a static string during
BucketDeployment.

**Why**: Provides a visible "last deployed" indicator in the UI without a runtime API call.
