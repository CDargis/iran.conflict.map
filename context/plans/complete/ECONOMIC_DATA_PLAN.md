# Economic Data Feature — Completed

**Completed:** 2026-03-31

---

## What Was Built

Brent crude price sparkline, Hormuz status pill, and per-date economic signals pane. Full
pipeline from extraction through frontend display.

---

## Components Shipped

### New DynamoDB Tables

**`iran-conflict-map-brent-prices`** — PK: `date`, SK: `timestamp`. Written by a dedicated
Brent Price Lambda on an EventBridge schedule (every 8 hours). ~3 rows per day accumulate;
no row is overwritten.

**`iran-conflict-map-economic-signals`** — PK: `date`, SK: `source_url`. Written by the Sync
Lambda when processing a CTP-ISW report. One row per (date, source_url); PutItem is idempotent.

GSI added to signals table: **`entity-date-index`** (PK: `entity`, SK: `date`, INCLUDE:
`hormuz_status`). All rows carry `entity = "signal"`. This enables efficient backwards range
queries for sticky Hormuz status without a full-table scan.

### Schema (as implemented)

```typescript
interface BrentPriceRow {
  date: string;       // PK — YYYY-MM-DD
  timestamp: string;  // SK — ISO 8601 UTC
  brent_price: number;
}

interface EconomicSignalRow {
  date: string;          // PK — YYYY-MM-DD
  source_url: string;    // SK — CTP-ISW report URL
  entity: string;        // always "signal" — required for GSI
  hormuz_status: "no_alert" | "open" | "restricted" | "closed";
  economic_notes: string[];
}
```

> `oil_export_volume_mbd` was dropped from the original plan — CTP-ISW reports rarely state
> a specific export volume figure.

### Hormuz Status Values

| Value | Meaning |
|---|---|
| `no_alert` | Report is silent on Hormuz — treat as unknown, use sticky |
| `open` | Report explicitly confirms normal/open passage |
| `restricted` | Report describes interference, seizures, mining, or legislative control |
| `closed` | Full blockade or closure |

### Sync Lambda (`Function.cs`)

Expanded Claude system prompt extracts `hormuz_status` and `economic_notes` from each
CTP-ISW report. Writes a row to the signals table after each sync. Stamps `entity = "signal"`
on every row.

### Backfill CLI (`backfill-signals` command in `IranConflictMap.Tools`)

Scans `syncs-v2` for completed sync records in a date range, fetches each report URL, calls
Claude Haiku, and writes to the signals table. Used to populate historical data.

Also: `stamp-signal-entity` command backfills the `entity = "signal"` attribute on rows
written before the GSI was added.

### API Endpoints

**`GET /api/economic/brent?from=YYYY-MM-DD&to=YYYY-MM-DD`**
Scans the brent-prices table. Historical dates: one row (latest timestamp). Today: all
intraday readings. 5-minute in-memory cache.

**`GET /api/economic/signals?date=YYYY-MM-DD`**
1. PK query for exact date. If row found and `hormuz_status != "no_alert"`, return it.
2. If no row or `no_alert` (report was silent): GSI query backwards (Limit=20), find first
   row with non-`no_alert` status. This is the sticky Hormuz result.
3. Returns `is_fallback: true` and `hormuz_as_of: "YYYY-MM-DD"` when sticky.
4. `economic_notes` always comes from the exact row for the requested date (if any).
5. All paths cached per date with 5-minute TTL.

Note: the planned combined `/api/economic` endpoint was not implemented. Brent and signals
are fetched independently.

### Frontend

**Brent sparkline** — SVG rendered in the topbar area. Loads once on page load. Vertical
dotted marker moves with selected date. Price tag on marker shows selected date's price.
Header label (`BRENT CRUDE $XX.XX`) always shows the most recent known price regardless of
selected date.

**Hormuz pill** — In topbar. Color-coded dot (green/amber/red). Glows when not `no_alert`.
When sticky, shows `* Status as of YYYY-MM-DD`.

**Economic Signals pane** — Panel above Sync Log (both desktop and mobile). Fetches
`/api/economic/signals?date=` on each date navigation. Shows `hormuz_status` line (when not
`no_alert`) and `economic_notes` bullets.

---

## Key Decisions Made During Implementation

See `context/decisions.md` for the following entries added during this feature:
- GSI on signals table instead of scan
- `no_alert` = silent (triggers sticky), `open` = explicitly confirmed
- Per-date fetch for signals (not upfront load)

---

## What Was Not Implemented (from original plan)

- Combined `/api/economic` endpoint — replaced by separate `/brent` and `/signals` endpoints
- Today box on sparkline (zoomed intraday section with separate y-axis)
- Hormuz status change dots on sparkline
- Sparkline windowing to selected date (sparkline always shows full range)
- `% change` label next to Brent price
- `predictions` field from Brent API stored/displayed
