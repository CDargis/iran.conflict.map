# Iran Conflict Map — Todo / In Progress

---

## Recently Completed

### Economic Signals Feature (2026-03-31)
Brent crude sparkline, Hormuz status pill, economic signals pane. Full pipeline: Sync Lambda
extraction, `backfill-signals` CLI, GSI on signals table for sticky Hormuz, per-date API
fetch, frontend display. See `plans/complete/ECONOMIC_DATA_PLAN.md`.

---

## Active / Incomplete

### Morning Reports Feature
**Status**: Architecture documented in `plans/active/MORNING_REPORTS_PLAN.md`. Nothing implemented.
**Blocker**: URL discovery mechanism for morning reports is unresolved. CTP-ISW morning reports
do not arrive via the email subscription. Must determine whether they need to be polled, scraped,
or submitted manually before any implementation begins.
**Schema changes needed**:
- `preliminary: true` (sparse boolean) on StrikeEvent
- `source: "morning"` (immutable string) on StrikeEvent
- New GSI: `preliminary-date-index` (PK: preliminary, SK: date) on strikes table
- System prompt update: pass morning/evening flag to Claude
- Processor: nuke-and-replace logic for preliminary events on evening sync

### Admin UI (review queue)
**Status**: Backend endpoints complete (`GET /api/review`, `POST /api/review/resolve`).
`admin.html` exists but the review queue UI is minimal.
**What's missing**: A usable interface to display review queue items (showing note, existing
record, proposed changes side-by-side) and buttons to approve as new / approve as update /
discard.

### DLQ Alerting
**Status**: Not implemented. The DLQ exists and items are routed to it, but there are no
CloudWatch alarms or SNS notifications when messages accumulate.
**What's needed**: CloudWatch metric filter or SQS alarm → SNS email (or similar notification).

### `update_log` persistence
**Status**: The `update_log` field is in the schema and the Processor Lambda has the logic
to append to it, but it appears to be commented out / not writing in the current code.
Verify whether this is intentional or an oversight.

---

## Cleanup / Low Priority

### `syncs` (legacy table)
The original `syncs` DynamoDB table is still defined in the CDK stack and exists in AWS but
nothing writes to it. Can be removed from the stack once confirmed safe.

### Seed data gap
Seed data in `seed/criticial-threats/` covers 2026-02-28 through approximately early March.
Any gap between the last seed file and when live email ingestion started represents missing
historical events.

### `frontend/strikes.json`
A static JSON file exists at `frontend/strikes.json` described as legacy/fallback data. It is
not referenced by the current API-driven frontend. Can be removed if confirmed unused.

---

## Deferred (from plans/complete/PLAN.md)

These were explicitly deferred during the Feb 2026 refactor and have not been addressed:
- DLQ alerting (above)
- Full admin UI for ambiguous item resolution (above)
- Audit trail dedicated rows (defined in morning reports plan, not implemented)
