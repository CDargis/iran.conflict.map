# Economic Data Feature — Implementation Plan

## Overview

Add structured economic indicators (Tier 1) and Claude-extracted economic signals (Tier 2) to the pipeline and frontend. Brent crude prices (sourced from ICE via a commodity API) are fetched by a **new dedicated Lambda on an EventBridge schedule (every 8 hours)**, keeping the price current throughout the day independent of report ingestion. Claude's extraction prompt is expanded in the existing Sync Lambda to pull economic signals (Tier 2) when a CTP-ISW report is processed. Two new DynamoDB tables hold Brent price readings and economic signals respectively. The API gets a new endpoint, and the frontend gains a sparkline, an indicator strip, and an economic tab in the event feed.

One new Lambda is added (Brent price poller). The Sync Lambda gains Claude prompt expansion and a Brent price fetch at report time. All other changes extend existing resources.

---

## 1. Schema Design

**Decided:** Two separate tables — one pure time-series ledger for Brent prices, one for Claude-extracted economic signals. No shared table, no `entity_type` discriminator, no GSI workarounds. Each table has a clean purpose.

---

### Table 1: `iran-conflict-map-brent-prices`

Simple time-series ledger. Written by the Brent Price Lambda on every scheduled invocation.

```typescript
interface BrentPriceRow {
  date: string;       // PK — YYYY-MM-DD (trading date in UTC)
  timestamp: string;  // SK — ISO 8601 (e.g. "2026-03-22T08:00:00Z"), UTC fetch time
  brent_price: number; // USD/barrel
  currency: "USD";
}
```

- ~3 rows accumulate per day. No row is ever overwritten.
- To query a single day's readings: `Query(PK = "2026-03-22")`.
- To query a date range for the sparkline: `Scan` with `FilterExpression date BETWEEN :from AND :to` (table is small — days × 3 rows, <10K items/year). A GSI can be added later if scan performance ever becomes a concern.

---

### Table 2: `iran-conflict-map-economic-signals`

Claude-extracted Tier 2 signals. Written by the Sync Lambda when a CTP-ISW report is processed.

```typescript
interface EconomicSignalRow {
  date: string;                         // PK — YYYY-MM-DD (report date)
  sync_id: string;                      // SK — ISO 8601 sync timestamp
  hormuz_status: "open" | "restricted" | "closed" | "unknown";
  oil_export_volume_mbd: number | null; // Million barrels/day; null if not reported
  economic_notes: string[];             // Sanctions, Treasury actions, infrastructure, etc.
  source_url: string;                   // CTP-ISW report URL
  synced_at: string;                    // ISO 8601 — same value as sync_id SK
}
```

- One row per sync run. Re-syncing the same date creates a second row (new `sync_id` SK). See open question 9B for how the API surfaces multiple rows.
- To query a date: `Query(PK = "2026-03-22")`.

---

No GSIs on either table. Cross-date queries on `brent-prices` use a Scan (acceptable at current scale). If the table grows or the scan becomes slow, a GSI (`timestamp` as SK on a global index) can be added without touching the application schema.

---

## 2. Data Source — Brent Crude Price

### Background

Brent crude trades on the **ICE (Intercontinental Exchange)**, not NYSE or a US stock exchange. The benchmark is the ICE Brent Crude futures contract. "Brent spot price" in financial data is derived from nearby futures. EIA and FRED publish end-of-day settlement prices sourced from ICE, but with a 1–2 business day reporting lag — which makes them unsuitable here. We want today's price at time of sync.

### Confirmed: Crude Price API (`crudepriceapi.com`)

Updates every 5 minutes, free forever (100 requests/month, no credit card required). At 3 calls/day (~90/month) we stay within the free tier with buffer.

```
GET https://api.crudepriceapi.com/v1/prices/latest
Headers: Authorization: Token {KEY}
```

Response shape (to be verified against actual docs):
```json
{ "price": 85.23, "currency": "USD", "updated_at": "2026-03-22T14:30:00.000Z" }
```

- `updated_at` is the quote timestamp — store as `brent_fetched_at`.
- Free tier: 100 requests/month, no credit card, no commercial use restrictions noted.
- No rate-limit concerns at ~90 req/month.

---

### Alternative Sources

| Source | Pros | Cons |
|--------|------|------|
| **Crude Price API** ✓ | Free forever (100 req/month), no CC, 5-min updates | Third-party service; response shape unverified until key obtained |
| **Oil Price API** | Near-real-time (ICE), ISO timestamp in response | Free tier limited; commercial terms unclear |
| **Alpha Vantage** | Free tier, near-real-time | Rate-limited to 25 req/day on free tier; Brent history requires premium |
| **Yahoo Finance (unofficial)** | No API key, ticker `BZ=F` | Unofficial scraping; futures ≠ spot; no SLA; legally gray |
| **EIA Open Data** | Free, official US govt data, history since 1987 | **1–2 business day lag — not acceptable for today's price** |
| **FRED (St. Louis Fed)** | Free, official, series `DCOILBRENTEU` | Same lag as EIA; identical underlying data |
| **Quandl / Nasdaq Data Link** | High quality | Paid for Brent spot |

**Note on EIA for backfill:** Even though EIA is unsuitable for live syncs (due to lag), its historical API is the right tool for the Tier 1 backfill operation (see Section 7A). Historical settlement prices are exactly what we want for past dates, and EIA's date-range endpoint makes batch fetching trivial.

---

## 3. Lambda Changes

### 3A. Sync Lambda (`src/IranConflictMap.Sync/Function.cs`)

One addition: an expanded Claude prompt that extracts economic signals, and a new DynamoDB write for the `ECONOMIC` row. The Sync Lambda does **not** call the Brent price API — that is entirely the Brent Price Lambda's responsibility. The two Lambdas write independent rows and do not coordinate.

#### Expanded Claude Prompt

The extraction system prompt (currently the inline `SystemPrompt` const in `Function.cs`) gains a second output block. The existing `new`/`updates`/`ambiguous` structure is unchanged.

Append to the prompt after the existing output instructions:

```
Additionally, extract economic signals from the same report text. Add a fourth key "economic"
to the top-level JSON response:

{
  "economic": {
    "hormuz_status": "open" | "restricted" | "closed" | "unknown",
    "oil_export_volume_mbd": number | null,
    "economic_notes": string[]
  }
}

Guidelines:
- hormuz_status: "restricted" or "closed" only if the report explicitly describes interference,
  mining, seizures, or blockade activity at the Strait of Hormuz. Default "open" if the report
  is silent. Use "unknown" only if the report explicitly acknowledges uncertainty.
- oil_export_volume_mbd: The figure in million barrels/day if the text states a specific volume
  for Iranian or regional oil exports. null if not mentioned.
- economic_notes: Flat array of concise complete sentences (one per distinct signal). Include:
  active or newly-announced sanctions and designations, OFAC/Treasury actions, energy
  infrastructure damage or threats, oil/gas price mentions tied to conflict activity, shipping
  insurance or Lloyd's notices, export bans or waivers, financial system impacts (SWIFT,
  correspondent banking). Omit general commentary with no actionable signal.
  Empty array [] if nothing qualifies.

The "economic" object is always present in the response, even if all fields are null/unknown/empty.
```

The JSON response parser (which already handles a top-level object from Claude's text response, stripping markdown code fences) will naturally pick up the `economic` key once the prompt asks for it.

#### Writing the Economic Signal Row

After parsing the Claude response, the Sync Lambda writes a row to `iran-conflict-map-economic-signals` via `PutItemAsync`.

```csharp
string syncedAt = DateTime.UtcNow.ToString("o");

Dictionary<string, AttributeValue> signalItem = new()
{
    ["date"]                   = new AttributeValue { S = reportDate },
    ["sync_id"]                = new AttributeValue { S = syncedAt },
    ["hormuz_status"]          = new AttributeValue { S = economic.HormuzStatus ?? "unknown" },
    ["oil_export_volume_mbd"]  = economic.OilExportVolumeMbd.HasValue
                                   ? new AttributeValue { N = economic.OilExportVolumeMbd.Value.ToString("F3") }
                                   : new AttributeValue { NULL = true },
    ["economic_notes"]         = new AttributeValue
                                   {
                                       L = (economic.EconomicNotes ?? [])
                                             .Select(n => new AttributeValue { S = n })
                                             .ToList()
                                   },
    ["source_url"]             = new AttributeValue { S = sourceUrl },
    ["synced_at"]              = new AttributeValue { S = syncedAt },
};

await _dynamoDb.PutItemAsync(new PutItemRequest
{
    TableName = _signalsTableName,  // env var SIGNALS_TABLE_NAME
    Item = signalItem,
});
```

**Re-sync behavior:** `sync_id` is the current timestamp, so a re-sync produces a new row — it does not overwrite the previous one. Multiple signal rows per date are possible. How the API surfaces them is an open question — see Section 9B.

#### SyncEnvelope / Processor Impact

No changes to `SyncEnvelope`. Economic data is written directly by the Sync Lambda; the processor queue carries only strike/update/ambiguous data as before.

### 3B. Processor Lambda (`src/IranConflictMap.Lambda/Function.cs`)

No changes required.

### 3C. API Lambda (`src/IranConflictMap.Api/Program.cs`)

New endpoint — see section 4.

### 3D. Brent Price Lambda — New (`src/IranConflictMap.Brent/Function.cs`)

A small new Lambda triggered by EventBridge on a fixed schedule (every 8 hours). Its only job: fetch the current Brent price and insert a `BRENT` row for today's date. No coordination with the Sync Lambda — it writes its own independent rows.

**Trigger:** EventBridge scheduled rule — `rate(8 hours)` or `cron(0 0/8 * * ? *)`. ICE is open Sunday 23:00 – Friday 22:00 UTC; no need to gate on market hours, the commodity API returns the last known quote when markets are closed.

**Logic:**
1. Call `FetchBrentPriceAsync()`.
2. If call fails, log and exit cleanly. Do not throw.
3. Write a row to `iran-conflict-map-brent-prices` via **`PutItemAsync`** — simple insert:

```csharp
string fetchedAt = DateTime.UtcNow.ToString("o");

await _dynamoDb.PutItemAsync(new PutItemRequest
{
    TableName = _brentTableName,  // env var BRENT_TABLE_NAME
    Item = new Dictionary<string, AttributeValue>
    {
        ["date"]        = new AttributeValue { S = DateTime.UtcNow.ToString("yyyy-MM-dd") },
        ["timestamp"]   = new AttributeValue { S = fetchedAt },
        ["brent_price"] = new AttributeValue { N = price.ToString("F2") },
        ["currency"]    = new AttributeValue { S = "USD" },
    },
});
```

Each invocation inserts a new row. ~3 rows accumulate per day. No row is ever overwritten.

**New files required:**
- `src/IranConflictMap.Brent/Function.cs` — Lambda handler
- `src/IranConflictMap.Brent/IranConflictMap.Brent.csproj` — project file (same structure as other Lambdas)

---

## 4. API Changes

Two new endpoints. The API queries each table independently and merges before returning.

### Endpoint 1: `GET /api/economic`

Per-day combined view. Queries both tables by `date` PK, merges results into one object.

**Query parameters:**
- `?date=YYYY-MM-DD` — single date; used by the economic tab and indicator strip.
- `?days=N` — last N days (default `30`, max `90`); returns one merged object per day.

**Implementation:** For each date, issue two `Query` calls in parallel:
1. `iran-conflict-map-brent-prices` (PK = date) → take the latest row by `timestamp` SK for `brent_price` / `brent_fetched_at`
2. `iran-conflict-map-economic-signals` (PK = date) → take the latest row by `sync_id` SK for Tier 2 fields

Merge into one response object. Each source is independently nullable if no row exists yet for that date.

**Response per date:**
```json
{
  "date": "2026-03-22",
  "brent_price": 85.23,
  "brent_fetched_at": "2026-03-22T16:00:00Z",
  "hormuz_status": "open",
  "oil_export_volume_mbd": null,
  "economic_notes": ["OFAC designated three Iranian tankers..."],
  "source_url": "https://www.criticalthreats.org/analysis/iran-update-march-22-2026",
  "synced_at": "2026-03-22T18:12:00Z"
}
```

### Endpoint 2: `GET /api/economic/brent`

Brent time-series for the chart. Queries `iran-conflict-map-brent-prices` directly.

The chart always covers the full dataset range (2026-02-28 to today) and is loaded once on page load. The `from`/`to` parameters exist for flexibility but the frontend always passes the full range — the endpoint does not need to support arbitrary date windows as a first-class use case.

**Query parameters:**
- `?from=YYYY-MM-DD&to=YYYY-MM-DD` — date range (inclusive). Frontend passes `from=2026-02-28&to=[today]`.

**Resolution logic (hardcoded, not client-configurable):**
- For all dates before today: return only the latest reading per day (group by `date`, keep row with latest `timestamp`).
- For today: return all readings accumulated so far.

This gives historical days one data point each and gives today higher resolution as readings accumulate throughout the day — the right edge of the chart updates intraday while the left edge stays stable.

**Implementation:** `Scan` on `iran-conflict-map-brent-prices` with `FilterExpression: #date BETWEEN :from AND :to`. The table is small (~3 rows/day, <10K items/year), so a scan is acceptable. Group in-memory: for dates before today keep only the latest-timestamp row; for today include all rows. A GSI on `date` can be added later if scan performance ever becomes an issue.

**Response:**
```json
[
  { "date": "2026-03-21", "brent_price": 84.90, "timestamp": "2026-03-21T16:00:00Z" },
  { "date": "2026-03-22", "brent_price": 85.10, "timestamp": "2026-03-22T08:00:00Z" },
  { "date": "2026-03-22", "brent_price": 85.23, "timestamp": "2026-03-22T16:00:00Z" }
]
```
*(Here 2026-03-22 is today — two intraday readings returned. Yesterday has one.)*

**Caching:** 5-minute in-memory cache on both endpoints. Negligible staleness given the 8-hour write cadence.

**Auth:** None (public).

---

## 5. Frontend Changes

All changes are in `frontend/index.html` (vanilla JS, no build step).

### 5A. Brent Crude Sparkline

Two distinct frontend use cases exist for Brent price data, each backed by a different API call:

**Chart behavior:**
- Fetches `GET /api/economic/brent?from=2026-02-28&to=[today]` once on page load
- Covers the full dataset range — not a rolling window, not date-navigation-aware
- Historical days return one data point each (latest reading of that day); today returns all intraday readings accumulated so far — right edge has higher resolution, by design
- Does **not** re-fetch or re-render when the user navigates dates

**Date marker:**
- A vertical line on the chart tracks the currently selected map date as the user navigates
- Lets the user see "when this event happened, oil was at this price"
- Implemented by re-drawing the marker SVG element on each date change — the underlying data and polyline do not change

**Sparkline markup** (nav bar):
```html
<div class="brent-sparkline-container">
  <svg id="brent-sparkline" width="200" height="32">
    <polyline id="brent-line" />
    <line id="brent-date-marker" y1="0" y2="32" stroke="#94a3b8" stroke-width="1" />
  </svg>
  <span id="brent-label">— $/bbl</span>
</div>
```

**X-axis layout — non-uniform spacing (today is special-cased):**
- Historical days are allocated equal horizontal width — uniform spacing, one point per day.
- Today is allocated proportionally more horizontal space to spread its intraday readings out cleanly.
- Concretely: partition the SVG width as `historicalWidth + todayWidth`, where `todayWidth` is large enough that intraday points aren't cramped (e.g. fixed 40px, or `N_intraday * 12px`). Historical days divide `historicalWidth` evenly.
- All x-coordinate calculations must account for this split — the x scale is not a simple linear map from date index to pixel.

**Line style — today vs historical:**
- Historical segment: solid line, amber (`#f59e0b`), one point per day, uniform spacing.
- Today's segment: all three visual treatments applied together to signal "in progress, not yet closed":
  1. **Dashed** — `stroke-dasharray` to break the line
  2. **Distinct color** — a different hue from the historical line (e.g. sky blue `#38bdf8` or slate `#94a3b8`), not a tint of amber
  3. **Muted/reduced opacity** — `opacity: 0.6` or equivalent, to visually recede relative to the historical segment
- Rendered as two separate `<polyline>` elements so styles don't bleed across the boundary.

**Live indicator:**
- A pulsing dot on the rightmost data point (latest reading) signals live data.
- Implemented as a `<circle>` with a CSS `@keyframes` pulse animation (scale or opacity).

**Date marker behavior with non-uniform layout:**
- When the selected date is a historical day, the marker falls in the uniform section — x computed from that day's slot.
- When the selected date is today, the marker falls in the expanded section — x computed relative to today's allocated width.
- Same re-draw-on-date-change approach; the marker does not affect data or polylines.

**Markup** (nav bar):
```html
<div class="brent-sparkline-container">
  <svg id="brent-sparkline" width="200" height="32">
    <polyline id="brent-line-historical" />
    <polyline id="brent-line-today" stroke-dasharray="3 2" />
    <circle id="brent-live-dot" r="3" />
    <line id="brent-date-marker" y1="0" y2="32" stroke="#94a3b8" stroke-width="1" />
  </svg>
  <span id="brent-label">— $/bbl</span>
</div>
```

**Rendering:** Vanilla JS — split data into historical points and today's points. Compute x positions using the non-uniform layout. Scale y using global `min`/`max` across all points. Draw historical polyline solid, today's polyline dashed. Place live dot at the last point. The `brent-label` shows the most recent price. On hover, optionally show `fetched_at`.

**Empty state:** Hide the container with `display:none` if the API returns no data.

### 5B. Slim Indicator Strip

A single fixed-height bar (~28px) below the sparkline, above the filter bar. Always visible.

```html
<div class="econ-strip">
  <span class="econ-item" id="econ-brent">Brent <strong>$85.23</strong> <em>+0.4%</em></span>
  <span class="econ-divider">·</span>
  <span class="econ-item" id="econ-hormuz">
    Hormuz <strong class="status-open">Open</strong>
  </span>
  <span class="econ-divider" id="econ-exports-divider" style="display:none">·</span>
  <span class="econ-item" id="econ-exports" style="display:none">
    Exports <strong>—</strong>
  </span>
</div>
```

**Data source:** Latest record from `/api/economic?days=1` (or the last item in the `days=30` response already fetched for the sparkline). Percent change calculated from the two most recent prices in the sparkline data.

**Hormuz status colors:**
- `open` → green (`#22c55e`)
- `restricted` → amber (`#f59e0b`)
- `closed` → red (`#ef4444`)
- `unknown` → muted gray (`#6b7280`)

**Exports field:** Hidden when `oil_export_volume_mbd` is null; shown as `X.X mb/d` when present.

### 5C. Economic Tab in Event Feed Panel

Third tab added to the bottom sheet alongside "Strikes" and "Syncs".

```html
<!-- existing tabs -->
<button class="tab-btn active" data-tab="strikes">Strikes</button>
<button class="tab-btn" data-tab="syncs">Syncs</button>
<!-- new -->
<button class="tab-btn" data-tab="economic">Economic</button>
```

```html
<div id="tab-economic" class="tab-panel" style="display:none">
  <div id="economic-notes-list"></div>
</div>
```

**Data source:** When the "Economic" tab is selected, fetch `GET /api/economic?date=YYYY-MM-DD` for the currently selected map date (`currentDate` variable). Tier 1 indicators displayed in a small header row; `economic_notes` rendered as a `<ul>`.

**Empty state:** "No economic signals extracted for [date]." — normal for quiet days. If no record exists at all for the date (backfill gap or date before feature was deployed): "No economic data for this date."

---

## 6. CDK / Infra Changes (`src/IranConflictMap/IranConflictMapStack.cs`)

### 6A. New DynamoDB Tables

```csharp
TableV2 brentTable = new TableV2(this, "BrentPricesTable", new TablePropsV2
{
    TableName    = "iran-conflict-map-brent-prices",
    PartitionKey = new Attribute { Name = "date",      Type = AttributeType.STRING },
    SortKey      = new Attribute { Name = "timestamp", Type = AttributeType.STRING },
    BillingMode  = BillingMode.PAY_PER_REQUEST,
    RemovalPolicy = RemovalPolicy.RETAIN,
});

TableV2 signalsTable = new TableV2(this, "EconomicSignalsTable", new TablePropsV2
{
    TableName    = "iran-conflict-map-economic-signals",
    PartitionKey = new Attribute { Name = "date",    Type = AttributeType.STRING },
    SortKey      = new Attribute { Name = "sync_id", Type = AttributeType.STRING },
    BillingMode  = BillingMode.PAY_PER_REQUEST,
    RemovalPolicy = RemovalPolicy.RETAIN,
});
```

### 6B. Brent Price Lambda

```csharp
Function brentFunction = new Function(this, "BrentFunction", new FunctionProps
{
    FunctionName = "iran-conflict-map-brent",
    Runtime      = Runtime.PROVIDED_AL2023,
    Architecture = Architecture.ARM_64,
    Handler      = "bootstrap",
    Code         = Code.FromAsset("src/IranConflictMap.Brent", new AssetOptions
    {
        Bundling = /* same CDK bundling config as other .NET Lambdas in the stack */
    }),
    Timeout      = Duration.Seconds(30),  // Simple HTTP call + one DynamoDB write
    MemorySize   = 256,
    Environment  = new Dictionary<string, string>
    {
        ["BRENT_API_KEY_PARAM"] = "/iran-conflict-map/brent_api_key",
        ["BRENT_TABLE_NAME"]    = brentTable.TableName,
    },
});
```

### 6C. EventBridge Schedule

```csharp
Rule brentSchedule = new Rule(this, "BrentSchedule", new RuleProps
{
    RuleName = "iran-conflict-map-brent-schedule",
    Schedule = Schedule.Rate(Duration.Hours(4)),
});

brentSchedule.AddTarget(new LambdaFunction(brentFunction));
```

Rate of `8 hours` means ~3 invocations/day (~90/month) — within Crude Price API's free tier of 100 req/month.

### 6D. IAM Grants

```csharp
signalsTable.GrantWriteData(syncFunction);   // PutItem — economic signal rows
brentTable.GrantWriteData(brentFunction);    // PutItem — Brent price rows
brentTable.GrantReadData(apiFunction);
signalsTable.GrantReadData(apiFunction);
```

### 6E. Environment Variables

```csharp
// Sync Lambda (existing block — add this line)
syncFunction.AddEnvironment("SIGNALS_TABLE_NAME", signalsTable.TableName);

// Brent Lambda (set in FunctionProps above — BRENT_TABLE_NAME already there)

// API Lambda (existing block — add these two lines)
apiFunction.AddEnvironment("BRENT_TABLE_NAME",   brentTable.TableName);
apiFunction.AddEnvironment("SIGNALS_TABLE_NAME", signalsTable.TableName);
```

The Brent Lambda reads the commodity API key at runtime from SSM via `GetParameterAsync` (`WithDecryption = true`). Do not inline the value as an env var. The Sync Lambda does not need the Brent API key.

### 6F. SSM Parameter for Brent API Key

Created out-of-band (value not in source):

```bash
aws ssm put-parameter \
  --name /iran-conflict-map/brent_api_key \
  --value "YOUR_CRUDE_PRICE_API_KEY" \
  --type SecureString \
  --region us-east-1
```

Grant the Brent Lambda SSM read access in CDK (Sync Lambda does not call the price API):

```csharp
IStringParameter brentKeyParam = StringParameter.FromSecureStringParameterAttributes(
    this, "BrentApiKeyParam",
    new SecureStringParameterAttributes
    {
        ParameterName = "/iran-conflict-map/brent_api_key",
        Version = 1,
    });

brentKeyParam.GrantRead(brentFunction);
```

### 6G. No Other New Resources

No new SQS queues or S3 buckets needed.

---

## 7. Historical Backfill

Data goes back to 2026-02-28. Without backfill, the sparkline will be incomplete and the economic tab will be empty for historical dates. Backfill is different for each tier.

### 7A. Tier 1 Backfill — Brent Prices (Straightforward)

For historical dates, EIA is the right source even though it's unsuitable for live syncs. Historical settlement prices are exactly what we want for past dates, and the 1–2 day lag is irrelevant when backfilling records from weeks ago. The near-real-time sources (Crude Price API, Oil Price API) are not designed for date-range historical fetches.

EIA provides historical Brent prices in a single API call:

```
GET https://api.eia.gov/v2/petroleum/pri/spt/data/
    ?api_key={EIA_KEY}
    &frequency=daily
    &data[0]=value
    &facets[product][]=RBRTE
    &start=2026-02-28
    &end=2026-03-21
    &sort[0][column]=period
    &sort[0][direction]=asc
    &length=100
```

This returns all trading days in the range. EIA omits weekends and holidays — that's correct, there's no Brent close on non-trading days.

The EIA key for backfill is separate from the live Brent API key. Register a free EIA key at https://www.eia.gov/opendata/ (instant). It is only needed for the one-time backfill run; it does not need to go into SSM or CDK.

**Implementation:** Add a `backfill-economic` command to `src/IranConflictMap.Tools/Program.cs`. It:
1. Calls EIA API with the full date range, using a key passed via CLI arg or env var.
2. For each returned `(period, value)` pair, writes one row to `iran-conflict-map-brent-prices`:
   - `date` = EIA `period` (YYYY-MM-DD)
   - `timestamp` = `"{period}T00:00:00Z"` — sentinel midnight UTC marks it as a historical settlement
   - `brent_price` = EIA settlement value (decimal)
   - `currency` = `"USD"`
3. Uses `PutItemAsync` with `ConditionExpression: attribute_not_exists(#ts)` (using `#ts` as an expression attribute name alias for the reserved word `timestamp`) to avoid overwriting any row already written by the live Lambda for the same key.

Historical rows coexist with live rows in the same table. The API and sparkline treat them identically.

**For weekends/holidays:** The sparkline should carry-forward the last known price for gap dates. Handle this in the frontend — connect adjacent data points without gaps.

### 7B. Tier 2 Backfill — Claude-Extracted Signals (Partial Options)

Historical CTP-ISW report HTML is no longer available through the email pipeline, but the URLs are known from several sources:

**Source A: `syncs-v2` table** — contains `report_url` for every URL that was processed through the live pipeline (~10 records currently, going back to when the pipeline went live). These are the best candidates: the report URLs are validated, and the pages may still be accessible on the CTP-ISW website.

**Source B: Seed data** — `seed/strikes-seed-batch1.json`, `strikes-seed-batch2.json`, `strikes-seed.json` contain `source_url` fields for seeded events. These URLs map to specific dates and can be used for backfill.

**Source C: CTP-ISW website** — Old Iran Update articles remain on `criticalthreats.org`. The `iran-update-{month}-{day}-{year}` URL pattern (already known to the Extract Lambda via the INI_LIST) can reconstruct URLs for past dates. The `test-ini-list` tool command already validates whether a slug exists.

**Options for Tier 2 backfill:**

**Option 1 — Extend `backfill-economic` to re-fetch and re-extract (Recommended)**

The Tools project already has `FetchReportTextAsync` logic (or can call the API). For each historical date:
1. Construct the report URL from the known date pattern.
2. Verify it exists (INI_LIST check or HTTP HEAD).
3. Fetch the page and call Claude with the economic-only portion of the extraction prompt.
4. Write `economic_notes`, `hormuz_status`, `oil_export_volume_mbd` into the existing record (UpdateItem to avoid overwriting the Brent data already backfilled).

This requires the Anthropic API key to be available to the Tools CLI (it already reads AWS credentials and SSM; adding SSM read for the Anthropic key is straightforward).

**Caveat:** This incurs Claude API costs (~$0.003/report at Sonnet 4.6 pricing for a shorter focused extraction). For ~22 days of backfill (Feb 28–Mar 21), cost is negligible (<$0.10).

**Option 2 — Skip Tier 2 for historical dates**

Accept that `economic_notes = []` for all pre-feature dates. The sparkline and Tier 1 indicators still work. The economic tab shows "No economic signals extracted" for historical dates. This is simple and might be the right call if the backfill effort isn't worth it.

**Option 3 — Manual entry via admin UI**

Not recommended. Too tedious, no structured interface exists for it.

**Recommendation:** Do Option 1 for dates where a valid CTP-ISW URL is constructible and the page exists. Skip Option 1 for dates where the page is no longer accessible. Combine with Option 2 for the remainder. The `backfill-economic` command should accept a `--no-claude` flag to skip the Tier 2 extraction and write Tier 1 only.

### 7C. Backfill Execution Order

1. Deploy CDK changes (Step 1 of implementation sequence).
2. Register a free EIA API key at https://www.eia.gov/opendata/ (for backfill only — not stored in SSM).
3. Build and run: `tools backfill-economic --start 2026-02-28 --end [today] --no-claude --eia-key YOUR_EIA_KEY` — writes Brent prices for all trading days.
4. Verify sparkline has 30 days of data in the frontend.
5. Optionally run: `tools backfill-economic --start 2026-02-28 --end [today] --only-claude` — re-fetches CTP-ISW pages and adds Tier 2 data for accessible URLs.

---

## 8. Implementation Sequencing

### Step 1 — CDK infrastructure (deploy first, no code changes)
1. Add the `iran-conflict-map-economic` DynamoDB table + GSI.
2. Add the Brent Price Lambda (`iran-conflict-map-brent`) with EventBridge schedule.
3. Add IAM grants for Sync, Brent, and API Lambdas.
4. Add SSM parameter reference + grant for Brent API key (both Lambdas).
5. Deploy: `cdk deploy`. Verify table exists and Brent Lambda is created in console.
6. Create SSM parameter: `aws ssm put-parameter --name /iran-conflict-map/brent_api_key --value "..." --type SecureString`.

### Step 2 — Brent Price Lambda: initial implementation
1. Create `src/IranConflictMap.Brent/` project with `Function.cs`.
2. Implement `FetchBrentPriceAsync()` and the `UpdateItemAsync` write.
3. Deploy. Invoke manually to verify it writes `brent_close` and `brent_fetched_at` for today's date in DynamoDB.
4. Verify the EventBridge schedule triggers it automatically (check CloudWatch Logs after 8 hours, or manually trigger from console).

### Step 3 — Sync Lambda: Brent price call at report time
1. Add `FetchBrentPriceAsync()` to `src/IranConflictMap.Sync/Function.cs` (can share implementation or duplicate — small enough that duplication is fine).
2. Log the result. Do not write to DynamoDB yet.
3. Deploy. Trigger manual sync, verify Brent price is logged in CloudWatch.

### Step 4 — Sync Lambda: Expanded Claude prompt + economic DynamoDB write
1. Expand `SystemPrompt` with the economic extraction block.
2. Add `EconomicExtraction` record/class (`HormuzStatus`, `OilExportVolumeMbd`, `EconomicNotes`).
3. Update JSON parse logic to extract the `economic` key from Claude response.
4. Add `PutItemAsync` (full record including Brent price) for the economic table.
5. Deploy. Trigger sync. Verify full economic record in DynamoDB — all fields present.

### Step 5 — API Lambda: `/api/economic` endpoint
1. Add `GET /api/economic` to `Program.cs`.
2. Implement GSI query + 5-minute cache.
3. Deploy. Verify via curl.

### Step 6 — Backfill
1. Add `backfill-economic` command to Tools project.
2. Run `--no-claude` to populate Brent prices for all historical trading days via EIA.
3. Verify sparkline data via the new API endpoint.
4. Optionally run Tier 2 (Claude) backfill.

### Step 7 — Frontend: sparkline + indicator strip
1. Add sparkline SVG and indicator strip markup to `index.html`.
2. Add JS: fetch `/api/economic?days=30`, render sparkline, populate strip.
3. Deploy via CDK.

### Step 8 — Frontend: economic tab
1. Add "Economic" tab button and panel to the bottom sheet.
2. Add JS: fetch and render economic notes when tab is selected.
3. Deploy.

---

## 9. Open Questions / Decisions Needed

### 9A. How Many Brent Rows to Surface Per Day
**Status: Resolved.** `GET /api/economic` (merged view) always uses the latest reading per date. `GET /api/economic/brent` hardcodes: historical dates return one row each (latest timestamp), today returns all accumulated readings. No `resolution` parameter — the behavior is fixed by the API. This gives the sparkline's right edge higher intraday resolution while keeping historical days stable.

### 9B. ECONOMIC Row Multi-Row Handling (Re-sync)
**Status: Blocking for API implementation.** Because the SK embeds the sync timestamp, re-processing a report creates a second ECONOMIC row for the same date rather than overwriting the first. The API currently takes the latest ECONOMIC row by SK sort order. Decide:
- **Latest wins** (current plan) — simple, may discard a more complete earlier extraction if a re-sync produces fewer notes
- **Merge** — union `economic_notes`, take latest `hormuz_status`; more complex but more resilient to partial re-extractions
- **Immutable** — first write wins; use `ConditionExpression: attribute_not_exists(sk)` on the ECONOMIC PutItem (similar to the strike event's immutable `description` design)

### 9C. API Response Shape: Merged Object vs Separate Objects
**Status: Blocking for frontend implementation.** The current plan has the API query both tables and merge into one combined object per date. Alternative: return them separately:
```json
{ "brent": { "price": 85.23, "fetched_at": "..." }, "signals": { "hormuz_status": "open", ... } }
```
Merged is simpler for most frontend use cases. Separate is more explicit about provenance and handles the case where only one table has data for a date. Decide before implementing `GET /api/economic`.

### 9D. Crude Price API Key + SSM
**Status: Blocking for Step 2.** Register at crudepriceapi.com (free, no credit card). Store the key in SSM: `aws ssm put-parameter --name /iran-conflict-map/brent_api_key --value "..." --type SecureString --region us-east-1`. Verify the actual response shape against the docs before implementing `FetchBrentPriceAsync()` — the shape in this plan is an approximation.

### 9E. Hormuz Status Default Behavior
Current plan: Claude defaults to `"open"` when the report is silent and `"unknown"` when the report explicitly acknowledges uncertainty. Verify this is the right semantic — `"open"` could be misleading if the report simply didn't cover the Strait that day. Alternative: default `"unknown"` always and only set `"open"` when the report explicitly confirms unrestricted transit. This is safer but will result in more `"unknown"` entries.

### 9F. Intraday Price vs. Daily Close
**Resolved: intraday.** The Brent Price Lambda runs every 8 hours throughout the day, so the stored price is always the most recent ICE quote at time of fetch. This is not the official daily settlement price (published by ICE after market close), but for a conflict map showing market reaction to geopolitical events, a live intraday quote is preferable to a lagged official close. If official settlement prices are ever needed, the EIA API (next-day lag) is the only free option.

### 9G. Brent Fetch Timestamp in UI
The strip shows "Brent $85.23" captured at a specific time. Consider whether to surface the timestamp at all — options:
- Show nothing; just the price (cleanest for a non-trading audience).
- Show "Brent $85.23 · as of 2:30 PM ET" on hover.
- Show nothing unless `brent_fetched_at` is more than 24 hours old (staleness warning only).

### 9H. Sparkline Width and Mobile Layout
200px × 32px was estimated. After implementing, check on mobile (the existing `.topbar` is `position:fixed; top:0`). The indicator strip adds ~28px more vertical space. On small screens this could compress the map. Consider making the strip optional/collapsible, or merging sparkline + strip into a single compact row.

### 9I. Backfill Tier 2 — Cost and Scope
Claude API cost for backfilling ~22 days of reports is negligible. Main constraint is whether old CTP-ISW report pages are still accessible. Verify before investing time in the backfill command. Run `tools test-ctp-fetch` against a Feb 2026 URL to confirm accessibility.

### 9J. Multiple Syncs per Day (Morning Reports)
Once morning reports are implemented (see `context/plans/active/MORNING_REPORTS_PLAN.md`), a date may produce two ECONOMIC rows — one from the morning report sync, one from the evening. This is naturally handled by the composite key design: each gets its own SK. How the API surfaces them (latest only, or merged) is covered by 9B above.

---

## File Reference Summary

| Area | File |
|------|------|
| Brent Price Lambda (new) | `src/IranConflictMap.Brent/Function.cs` (create) |
| Sync Lambda handler + prompt | `src/IranConflictMap.Sync/Function.cs` |
| Processor Lambda (no changes) | `src/IranConflictMap.Lambda/Function.cs` |
| API Lambda endpoints | `src/IranConflictMap.Api/Program.cs` |
| CDK stack | `src/IranConflictMap/IranConflictMapStack.cs` |
| Tools CLI (new backfill command) | `src/IranConflictMap.Tools/Program.cs` |
| Frontend (all UI changes) | `frontend/index.html` |
| Schema context (update after deploy) | `context/schema.md` |
| Architecture context (update after deploy) | `context/architecture.md` |
