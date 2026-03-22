# Economic Data Feature — Implementation Plan

## Overview

Add structured economic indicators (Tier 1) and Claude-extracted economic signals (Tier 2) to the pipeline and frontend. EIA (or alternative source) Brent crude prices are fetched in the Sync Lambda alongside existing CTP-ISW report processing. Claude's extraction prompt is expanded to pull economic signals from the same report text. A new DynamoDB table holds per-day economic records. The API gets a new endpoint, and the frontend gains a sparkline, an indicator strip, and an economic tab in the event feed.

Nothing is deployed as a standalone Lambda. All changes extend existing resources.

---

## 1. Schema Design — Open Question

The schema approach is a real decision point. Two concrete options:

---

### Option A — Separate `iran-conflict-map-economic` table (Recommended)

One record per calendar day, keyed on the CTP-ISW report date.

```typescript
interface EconomicRecord {
  date: string;                         // PK — YYYY-MM-DD (report date)
  entity: "economic";                   // Constant — used for GSI range queries

  // Tier 1 — EIA-sourced
  brent_close: number | null;           // USD/barrel; null if unavailable
  brent_date: string | null;            // Actual EIA observation date (often lags 1–2 days)

  // Tier 1 — Claude-extracted from report
  hormuz_status: "open" | "restricted" | "closed" | "unknown";
  oil_export_volume_mbd: number | null; // Million barrels/day; null if not reported

  // Tier 2 — Claude-extracted freeform signals
  economic_notes: string[];             // Sanctions, Treasury actions, infrastructure, etc.

  // Provenance
  source_url: string;                   // CTP-ISW report URL
  synced_at: string;                    // ISO 8601; overwritten on re-sync
}
```

**Key design:**
- PK is `date` (String). One record per day; re-running a sync for the same date does a PutItem overwrite.
- A GSI (`entity-date-index`, PK: `entity`, SK: `date`) enables efficient range queries for the sparkline. This mirrors the existing `entity-date-index` on the strikes table — same access pattern.
- `brent_close` and `brent_date` are separate because EIA data lags 1–2 business days. The reported price may be for the previous trading day; capturing `brent_date` lets the UI display accurately.

**Pros:**
- Clean separation from strike data; no cross-contamination of very different record shapes.
- Existing strike table queries are completely unaffected — no risk of accidentally returning economic records.
- Economic data has different access patterns (range scan for sparkline; point lookup for tab) that are cleaner on a dedicated table.
- Easier to reason about, cheaper to debug, trivial to drop if requirements change.
- Follows the same provisioning model as existing tables (PAY_PER_REQUEST, RETAIN removal policy).

**Cons:**
- New table to manage, provision, and grant IAM for (minor).
- Doesn't follow a strict single-table DynamoDB pattern (this project already uses two tables, so not a real concern).

---

### Option B — Add economic records to the existing `strikes` table

Use the same `strikes` table with `entity: "economic"` to store economic records. The existing `entity-date-index` GSI already supports this pattern (it's a generic entity+date index).

```typescript
// In the strikes table, an economic record would look like:
interface EconomicRecordInStrikesTable {
  id: string;         // GUID (required by table PK; synthetic for economic records)
  entity: "economic"; // New entity value; reuses existing GSI
  date: string;       // SK on entity-date-index
  brent_close?: number;
  hormuz_status?: string;
  economic_notes?: string[];
  // ... all other economic fields
  // Note: lat, lng, severity, actor, etc. are absent — sparse item
}
```

**Pros:**
- No new table; reuses existing GSI infrastructure.
- Pure single-table DynamoDB design.

**Cons:**
- The strikes table's GSI (`entity-date-index`) is currently used to scan all items with `entity = "strike"`. Adding `entity = "economic"` items to the same table means API queries for strikes must be careful to filter — currently they rely on `entity = "strike"` which would continue to work, but it's a silent trap.
- Economic and strike records have almost no field overlap. Sparse items with many null/absent fields are technically fine in DynamoDB but cognitively confusing.
- Makes the strikes table's semantics muddier: it stops being a "strike events" table and becomes "all conflict-related records."
- The API Lambda's strikes caching and Processor Lambda logic would need to be verified to not accidentally touch economic records.

---

### Recommendation: **Option A**

The clean separation is worth the minor overhead of one new table. The risk of the single-table approach contaminating existing strike queries isn't worth the "elegance" of fewer tables, especially since this project already uses multiple tables.

**Decision needed:** Confirm Option A before Step 1 of implementation.

---

## 2. Data Source — Brent Crude Price

### Primary Recommendation: EIA Open Data API v2

The U.S. Energy Information Administration provides free historical and near-real-time Brent crude daily closing prices.

**Registration:** Free API key at https://www.eia.gov/opendata/ — no approval process, typically available within minutes.

**Endpoint:**
```
GET https://api.eia.gov/v2/petroleum/pri/spt/data/
    ?api_key={KEY}
    &frequency=daily
    &data[0]=value
    &facets[product][]=RBRTE
    &sort[0][column]=period
    &sort[0][direction]=desc
    &length=1
```

- **Series ID:** `RBRTE` — Europe Brent Spot Price FOB (Dollars per Barrel). This is the standard Brent benchmark used in financial reporting and geopolitical analysis.
- **Alternative series:** `RWTC` is WTI (West Texas Intermediate) — slightly different but also valid. Brent (`RBRTE`) is more relevant for Middle East / geopolitical context.
- **Historical depth:** Available from May 1987 onward — more than sufficient for backfill.

**Response shape:**
```json
{
  "response": {
    "data": [
      { "period": "2026-03-21", "value": "85.23", "product": "RBRTE", "duoarea": "R2", "process": "SPT" }
    ]
  }
}
```

**Known gotchas:**
1. **Lag:** EIA publishes daily spot prices with a 1–2 business day lag. Weekends and US holidays extend the gap. This means on a Monday sync, `period` might be the previous Thursday. Always store `brent_date` alongside `brent_close` so the UI can show "as of [date]" rather than implying it's today's price.
2. **Rate limits:** Generous for free tier (no documented hard limit, but EIA recommends staying under a few thousand requests/day for free keys). Not a concern for daily syncs.
3. **Missing data:** EIA omits weekends and US market holidays. The `?length=1` query will return the most recent available trading day, which is correct behavior.
4. **Value is a string:** `"value": "85.23"` — parse as decimal, not integer.
5. **No real-time data:** EIA is not a market data feed. For the purposes of this feature (daily conflict context, not trading), this is fine.

**For historical backfill:** Use `&sort[0][direction]=asc&start=2026-02-28&end=2026-03-22` to fetch a date range in one call. Limit is 5000 rows per request, sufficient for any realistic range.

---

### Alternative Sources

| Source | Pros | Cons |
|--------|------|------|
| **EIA (recommended)** | Free, no rate limits for our usage, historical depth since 1987, official US government data | 1–2 day lag |
| **Alpha Vantage** | Free tier, near-real-time, many commodities | Rate-limited to 25 req/day on free tier; `BRENT` commodity requires premium for some history |
| **Yahoo Finance (unofficial)** | No API key, ticker `BZ=F` for Brent futures | Unofficial / unofficial scraping; no SLA; futures price ≠ spot price; legally gray |
| **Quandl / Nasdaq Data Link** | High quality, good documentation | Paid for most series; Brent spot needs a paid subscription |
| **FRED (St. Louis Fed)** | Free, official, series `DCOILBRENTEU` | Same lag as EIA; slightly more cumbersome API; identical underlying data |

**Conclusion:** EIA is the right choice. FRED is a valid backup (same data, different API). If this ever needs sub-day prices, Alpha Vantage or a paid provider would be required.

---

## 3. Lambda Changes

### 3A. Sync Lambda (`src/IranConflictMap.Sync/Function.cs`)

Two additions: an EIA HTTP call and an expanded Claude prompt. Both happen within the existing handler, after the report text is fetched and before the SyncEnvelope is pushed to the processor queue.

#### EIA Call

After `FetchReportTextAsync` succeeds, call the EIA API for the most recent Brent closing price. New private method: `FetchBrentPriceAsync()` returning `(decimal? Close, string? ObservationDate)`.

- If the EIA call fails (network error, bad API key, malformed response), log a warning and continue with `null`/`null`. **Do not fail the sync.** EIA data is supplemental.
- Use the existing static `HttpClient` already in the Sync Lambda — same pattern as `FetchReportTextAsync`.
- The EIA API key is read at cold start from SSM (`/iran-conflict-map/eia_api_key`) using `GetParameterAsync` with `WithDecryption = true`, mirroring the existing Anthropic key pattern.

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

#### Writing the Economic Record

After parsing the Claude response, the Sync Lambda writes directly to `iran-conflict-map-economic` via `PutItemAsync`. This does **not** route through the processor queue — economic data has no proximity matching, review queue, or dedup requirements.

```csharp
// After Claude parse, before enqueuing to processor
Dictionary<string, AttributeValue> economicItem = new()
{
    ["date"]                   = new AttributeValue { S = reportDate },
    ["entity"]                 = new AttributeValue { S = "economic" },
    ["brent_close"]            = brentPrice.Close.HasValue
                                   ? new AttributeValue { N = brentPrice.Close.Value.ToString("F2") }
                                   : new AttributeValue { NULL = true },
    ["brent_date"]             = brentPrice.ObservationDate != null
                                   ? new AttributeValue { S = brentPrice.ObservationDate }
                                   : new AttributeValue { NULL = true },
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
    ["synced_at"]              = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
};

await _dynamoDb.PutItemAsync(new PutItemRequest
{
    TableName = _economicTableName,  // from env var ECONOMIC_TABLE_NAME
    Item = economicItem,
});
```

**Re-sync behavior:** PutItem replaces the existing record. If a date is re-synced (DLQ retry, manual trigger), economic data refreshes. Intentional — EIA price may have updated since the first sync.

#### SyncEnvelope / Processor Impact

No changes to `SyncEnvelope`. Economic data is written directly by the Sync Lambda; the processor queue carries only strike/update/ambiguous data as before.

### 3B. Processor Lambda (`src/IranConflictMap.Lambda/Function.cs`)

No changes required.

### 3C. API Lambda (`src/IranConflictMap.Api/Program.cs`)

New endpoint — see section 4.

---

## 4. API Changes

### New Endpoint: `GET /api/economic`

Added to `Program.cs` alongside the existing strike/sync endpoints.

**Query parameters:**
- `?days=N` — return the last N days of records (default `30`, max `90`). Used by the sparkline.
- `?date=YYYY-MM-DD` — single record for a specific date.
- These are mutually exclusive; `date` takes precedence if both are provided.

**Response (array form, `?days=N`):**
```json
[
  {
    "date": "2026-03-21",
    "brent_close": 85.23,
    "brent_date": "2026-03-20",
    "hormuz_status": "open",
    "oil_export_volume_mbd": null,
    "economic_notes": ["OFAC designated three Iranian tankers operating under Venezuelan flag..."],
    "source_url": "https://www.criticalthreats.org/analysis/iran-update-march-21-2026",
    "synced_at": "2026-03-22T04:12:00Z"
  }
]
```

**Response (single record, `?date=YYYY-MM-DD`):** Same shape, not wrapped in array. Returns 404 if no record exists for that date.

**Implementation:** Query the `entity-date-index` GSI with `entity = "economic"` and `date BETWEEN [start] AND [end]` for range queries, or `date = [date]` for single-date lookup. Mirror the pattern used in `GET /api/strikes`.

**Caching:** 5-minute in-memory cache keyed on query parameters, identical to the strikes caching pattern.

**Auth:** None (public, same as `/api/strikes`).

---

## 5. Frontend Changes

All changes are in `frontend/index.html` (vanilla JS, no build step).

### 5A. Brent Crude Sparkline

**Placement:** Below the date navigation arrows, centered, spanning the nav bar width. The arrows stay at their current size; the sparkline occupies the space beneath them.

**Markup** (added to the topbar/date-nav zone):
```html
<div class="brent-sparkline-container">
  <svg id="brent-sparkline" width="200" height="32"></svg>
  <span id="brent-label">— $/bbl</span>
</div>
```

**Rendering:** Vanilla JS — scale 30 data points to the SVG viewport using `min`/`max`, draw a `<polyline>`. Color: amber (`#f59e0b`). The `brent-label` shows the most recent price. On hover, show a tooltip with the observation date (important because of EIA lag).

**Data fetch:** On page load, call `GET /api/economic?days=30`. Cache in a module-level variable. Not re-fetched on date navigation — sparkline is always 30-day trailing, independent of the selected map date.

**Empty state:** If no economic records exist yet, hide the container with `display:none`.

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

### 6A. New DynamoDB Table

```csharp
TableV2 economicTable = new TableV2(this, "EconomicTable", new TablePropsV2
{
    TableName = "iran-conflict-map-economic",
    PartitionKey = new Attribute { Name = "date", Type = AttributeType.STRING },
    BillingMode = BillingMode.PAY_PER_REQUEST,
    GlobalSecondaryIndexes = new[]
    {
        new GlobalSecondaryIndexPropsV2
        {
            IndexName = "entity-date-index",
            PartitionKey = new Attribute { Name = "entity", Type = AttributeType.STRING },
            SortKey    = new Attribute { Name = "date",   Type = AttributeType.STRING },
        }
    },
    RemovalPolicy = RemovalPolicy.RETAIN,
});
```

### 6B. IAM Grants

```csharp
economicTable.GrantWriteData(syncFunction);
economicTable.GrantReadData(apiFunction);
```

### 6C. Environment Variables

```csharp
// Sync Lambda
syncFunction.AddEnvironment("EIA_API_KEY_PARAM",   "/iran-conflict-map/eia_api_key");
syncFunction.AddEnvironment("ECONOMIC_TABLE_NAME",  economicTable.TableName);

// API Lambda
apiFunction.AddEnvironment("ECONOMIC_TABLE_NAME",   economicTable.TableName);
```

The EIA key is read at runtime by the Sync Lambda via SSM `GetParameterAsync` (`WithDecryption = true`), same pattern as the existing Anthropic key. Do not inline the value as an env var.

### 6D. SSM Parameter for EIA API Key

The parameter is a SecureString created out-of-band (not by CDK, since the value isn't in source):

```bash
aws ssm put-parameter \
  --name /iran-conflict-map/eia_api_key \
  --value "YOUR_EIA_KEY" \
  --type SecureString \
  --region us-east-1
```

In the CDK stack, grant the Sync Lambda read access:

```csharp
IStringParameter eiaKeyParam = StringParameter.FromSecureStringParameterAttributes(
    this, "EiaApiKeyParam",
    new SecureStringParameterAttributes
    {
        ParameterName = "/iran-conflict-map/eia_api_key",
        Version = 1,
    });

eiaKeyParam.GrantRead(syncFunction);
```

### 6E. No Other New Resources

No new SQS queues, S3 buckets, or EventBridge rules needed.

---

## 7. Historical Backfill

Data goes back to 2026-02-28. Without backfill, the sparkline will be incomplete and the economic tab will be empty for historical dates. Backfill is different for each tier.

### 7A. Tier 1 Backfill — Brent Prices (Straightforward)

EIA provides historical Brent prices in a single API call using date range parameters:

```
GET https://api.eia.gov/v2/petroleum/pri/spt/data/
    ?api_key={KEY}
    &frequency=daily
    &data[0]=value
    &facets[product][]=RBRTE
    &start=2026-02-28
    &end=2026-03-21
    &sort[0][column]=period
    &sort[0][direction]=asc
    &length=100
```

This returns all trading days in the range. Note that EIA omits weekends and holidays, so `period` will have gaps. That's correct — there's no Brent close on non-trading days.

**Implementation:** Add a `backfill-economic` command to `src/IranConflictMap.Tools/Program.cs`. It:
1. Calls the EIA API with the full date range.
2. For each returned `(period, value)` pair, writes a minimal economic record to DynamoDB:
   - `brent_close` and `brent_date` populated from EIA.
   - `hormuz_status = "unknown"` (not yet extracted).
   - `oil_export_volume_mbd = null`.
   - `economic_notes = []`.
   - `source_url = ""` (no report URL for these placeholder records).
3. Does NOT overwrite records that already have a non-empty `source_url` (use `ConditionExpression: attribute_not_exists(source_url) OR source_url = :empty`).

This creates placeholder Brent price records for all historical trading days. When a date is later re-synced through the live pipeline, the PutItem overwrites the placeholder with full data.

**For weekends/holidays:** The sparkline should interpolate or carry-forward the last known price. Handle this in the frontend (not the API) — connect adjacent data points without gaps.

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
2. Put EIA API key in SSM.
3. Build and run: `tools backfill-economic --start 2026-02-28 --end [today] --no-claude` — writes Brent prices for all trading days.
4. Verify sparkline has 30 days of data in the frontend.
5. Optionally run: `tools backfill-economic --start 2026-02-28 --end [today] --only-claude` — re-fetches CTP-ISW pages and adds Tier 2 data for accessible URLs.

---

## 8. Implementation Sequencing

### Step 1 — CDK infrastructure (deploy first, no code changes)
1. Add the `iran-conflict-map-economic` DynamoDB table + GSI to the CDK stack.
2. Add IAM grants for Sync and API Lambdas.
3. Add SSM parameter reference + grant for EIA key.
4. Deploy: `cdk deploy`. Verify table exists in console.
5. Manually create SSM parameter with real EIA API key.

### Step 2 — Sync Lambda: EIA call
1. Add `FetchBrentPriceAsync()` to `Function.cs`. Wire into handler after `FetchReportTextAsync`.
2. Log the result. Do not write to DynamoDB yet.
3. Deploy. Trigger manual sync, verify EIA call succeeds in CloudWatch logs.

### Step 3 — Sync Lambda: Expanded Claude prompt + economic DynamoDB write
1. Expand `SystemPrompt` constant with the economic extraction block.
2. Add `EconomicExtraction` record/class (`HormuzStatus`, `OilExportVolumeMbd`, `EconomicNotes`).
3. Update JSON parse logic to extract `economic` key from Claude response.
4. Add `PutItemAsync` call for the economic table.
5. Deploy. Trigger sync. Verify economic record in DynamoDB with correct fields.

### Step 4 — API Lambda: `/api/economic` endpoint
1. Add `GET /api/economic` to `Program.cs`.
2. Implement GSI query + 5-minute cache.
3. Deploy. Verify endpoint via curl.

### Step 5 — Backfill
1. Add `backfill-economic` command to Tools project.
2. Run with `--no-claude` first to populate Brent prices for all historical trading days.
3. Verify sparkline data is present via the new API endpoint.
4. Optionally run Tier 2 backfill.

### Step 6 — Frontend: sparkline + indicator strip
1. Add sparkline SVG and indicator strip markup to `index.html`.
2. Add JS: fetch `/api/economic?days=30`, render sparkline, populate strip.
3. Deploy via CDK (triggers `BucketDeployment`).

### Step 7 — Frontend: economic tab
1. Add "Economic" tab button and panel to the bottom sheet.
2. Add JS: fetch and render economic notes when tab is selected.
3. Deploy.

---

## 9. Open Questions / Decisions Needed

### 9A. Schema: Option A vs B
**Status: Blocking.** See Section 1. Recommendation is Option A (separate table). Confirm before Step 1.

### 9B. EIA API Key
**Status: Blocking for Step 2.** Register at https://www.eia.gov/opendata/. Free, instant. Takes ~5 minutes.

### 9C. Hormuz Status Default Behavior
Current plan: Claude defaults to `"open"` when the report is silent and `"unknown"` when the report explicitly acknowledges uncertainty. Verify this is the right semantic — `"open"` could be misleading if the report simply didn't cover the Strait that day. Alternative: default `"unknown"` always and only set `"open"` when the report explicitly confirms unrestricted transit. This is safer but will result in more `"unknown"` entries.

### 9D. EIA Data Lag in UI
The strip shows "Brent $85.23" but the price may be 2 days old. Options:
- Show "Brent $85.23 (Mar 20)" — date next to price, always explicit.
- Show "Brent $85.23 · 2d ago" — relative lag indicator.
- Show nothing for lag; only display if `brent_date` is within 3 calendar days.

### 9E. Sparkline Width and Mobile Layout
200px × 32px was estimated. After implementing, check on mobile (the existing `.topbar` is `position:fixed; top:0`). The indicator strip adds ~28px more vertical space. On small screens this could compress the map. Consider making the strip optional/collapsible, or merging sparkline + strip into a single compact row.

### 9F. Backfill Tier 2 — Cost and Scope
Claude API cost for backfilling ~22 days of reports is negligible. Main constraint is whether old CTP-ISW report pages are still accessible. Verify before investing time in the backfill command. Run `tools test-ctp-fetch` against a Feb 2026 URL to confirm accessibility.

### 9G. Multiple Syncs per Day (Morning Reports)
Once morning reports are implemented (see `context/plans/active/MORNING_REPORTS_PLAN.md`), a date may be synced twice. The second sync's PutItem overwrites the economic record. This is acceptable — Claude's second extraction has more complete information. If this causes data loss in practice, add `UpdateItem` that merges `economic_notes` arrays instead of replacing.

---

## File Reference Summary

| Area | File |
|------|------|
| Sync Lambda handler + prompt | `src/IranConflictMap.Sync/Function.cs` |
| Processor Lambda (no changes) | `src/IranConflictMap.Lambda/Function.cs` |
| API Lambda endpoints | `src/IranConflictMap.Api/Program.cs` |
| CDK stack | `src/IranConflictMap/IranConflictMapStack.cs` |
| Tools CLI (new backfill command) | `src/IranConflictMap.Tools/Program.cs` |
| Frontend (all UI changes) | `frontend/index.html` |
| Schema context (update after deploy) | `context/schema.md` |
| Architecture context (update after deploy) | `context/architecture.md` |
