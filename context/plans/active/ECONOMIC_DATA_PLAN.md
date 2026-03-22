# Economic Data Feature — Implementation Plan

## Overview

Add structured economic indicators (Tier 1) and Claude-extracted economic signals (Tier 2) to the pipeline and frontend. Near-real-time Brent crude prices (sourced from ICE via a commodity API) are fetched in the Sync Lambda alongside existing CTP-ISW report processing. Claude's extraction prompt is expanded to pull economic signals from the same report text. A new DynamoDB table holds per-day economic records. The API gets a new endpoint, and the frontend gains a sparkline, an indicator strip, and an economic tab in the event feed.

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

  // Tier 1 — commodity API sourced (ICE Brent)
  brent_close: number | null;           // USD/barrel; null if API call failed
  brent_fetched_at: string | null;      // ISO 8601 timestamp of when the price was fetched

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
- `brent_close` and `brent_fetched_at` are stored together so the UI can show the price alongside when it was captured. Near-real-time sources still have some delay (minutes, not days), and capturing the fetch timestamp makes that transparent.

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

### Background

Brent crude trades on the **ICE (Intercontinental Exchange)**, not NYSE or a US stock exchange. The benchmark is the ICE Brent Crude futures contract. "Brent spot price" in financial data is derived from nearby futures. EIA and FRED publish end-of-day settlement prices sourced from ICE, but with a 1–2 business day reporting lag — which makes them unsuitable here. We want today's price at time of sync.

### Primary Recommendation: API Ninjas or Oil Price API

Both sources pull from ICE and deliver near-real-time quotes (minutes-delayed, sufficient for a daily conflict map context). Both have free tiers that cover one call per day with room to spare.

**Option 1 — API Ninjas** (`api-ninjas.com`)

```
GET https://api.api-ninjas.com/v1/commodityprice?name=brent_crude_oil
Headers: X-Api-Key: {KEY}
```

Response shape:
```json
{ "name": "brent_crude_oil", "price": 85.23, "updated": 1742680200 }
```

- Free tier: 50,000 requests/month — far more than needed.
- `updated` is a Unix timestamp of the last ICE quote.
- No rate-limit concerns for one daily call.
- Registration: https://api-ninjas.com — free, instant API key.

**Option 2 — Oil Price API** (`oilpriceapi.com`)

```
GET https://api.oilpriceapi.com/v1/prices/latest
    ?by_code=BRENT_CRUDE_USD
Headers: Authorization: Token {KEY}
```

Response shape:
```json
{ "status": "success", "data": { "price": 85.23, "formatted": "85.23 USD", "currency": "USD", "code": "BRENT_CRUDE_USD", "created_at": "2026-03-22T14:30:00.000Z", "type": "spot_price" } }
```

- Free tier: 1,000 requests/month — sufficient for one call/day.
- `created_at` is the ICE quote timestamp in ISO 8601 — store this as `brent_fetched_at`.
- Registration: https://oilpriceapi.com — free tier available.

**Recommendation:** Either works. API Ninjas has a more generous free tier and simpler response shape. Oil Price API's `created_at` field maps more naturally to the schema. Pick based on whichever key is easier to obtain; the Lambda call is trivial to swap.

---

### Alternative Sources

| Source | Pros | Cons |
|--------|------|------|
| **API Ninjas** | Near-real-time (ICE), generous free tier, simple API | Third-party service, not an official exchange feed |
| **Oil Price API** | Near-real-time (ICE), ISO timestamp in response | Smaller free tier (1K req/month) |
| **Alpha Vantage** | Free tier, near-real-time | Rate-limited to 25 req/day on free tier; Brent history requires premium |
| **Yahoo Finance (unofficial)** | No API key, ticker `BZ=F` | Unofficial scraping; futures ≠ spot; no SLA; legally gray |
| **EIA Open Data** | Free, official US govt data, history since 1987 | **1–2 business day lag — not acceptable for today's price** |
| **FRED (St. Louis Fed)** | Free, official, series `DCOILBRENTEU` | Same lag as EIA; identical underlying data |
| **Quandl / Nasdaq Data Link** | High quality | Paid for Brent spot |

**Note on EIA for backfill:** Even though EIA is unsuitable for live syncs (due to lag), its historical API is the right tool for the Tier 1 backfill operation (see Section 7A). Historical settlement prices are exactly what we want for past dates, and EIA's date-range endpoint makes batch fetching trivial.

---

## 3. Lambda Changes

### 3A. Sync Lambda (`src/IranConflictMap.Sync/Function.cs`)

Two additions: a commodity API HTTP call for Brent price and an expanded Claude prompt. Both happen within the existing handler, after the report text is fetched and before the SyncEnvelope is pushed to the processor queue.

#### Brent Price Call

After `FetchReportTextAsync` succeeds, call the commodity API (API Ninjas or Oil Price API) for the current Brent price. New private method: `FetchBrentPriceAsync()` returning `(decimal? Price, string? FetchedAt)` where `FetchedAt` is an ISO 8601 timestamp.

- If the call fails (network error, bad API key, malformed response), log a warning and continue with `null`/`null`. **Do not fail the sync.** Brent data is supplemental.
- Use the existing static `HttpClient` already in the Sync Lambda — same pattern as `FetchReportTextAsync`.
- The commodity API key is read at cold start from SSM (`/iran-conflict-map/brent_api_key`) using `GetParameterAsync` with `WithDecryption = true`, mirroring the existing Anthropic key pattern.

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
    ["brent_close"]            = brentPrice.Price.HasValue
                                   ? new AttributeValue { N = brentPrice.Price.Value.ToString("F2") }
                                   : new AttributeValue { NULL = true },
    ["brent_fetched_at"]       = brentPrice.FetchedAt != null
                                   ? new AttributeValue { S = brentPrice.FetchedAt }
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

**Re-sync behavior:** PutItem replaces the existing record. If a date is re-synced (DLQ retry, manual trigger), economic data refreshes with the current live price and a new `brent_fetched_at` timestamp.

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
    "brent_fetched_at": "2026-03-22T14:30:00Z",
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

**Rendering:** Vanilla JS — scale 30 data points to the SVG viewport using `min`/`max`, draw a `<polyline>`. Color: amber (`#f59e0b`). The `brent-label` shows the most recent price. On hover, optionally show the `brent_fetched_at` timestamp.

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
syncFunction.AddEnvironment("BRENT_API_KEY_PARAM",  "/iran-conflict-map/brent_api_key");
syncFunction.AddEnvironment("ECONOMIC_TABLE_NAME",  economicTable.TableName);

// API Lambda
apiFunction.AddEnvironment("ECONOMIC_TABLE_NAME",   economicTable.TableName);
```

The Brent API key is read at runtime by the Sync Lambda via SSM `GetParameterAsync` (`WithDecryption = true`), same pattern as the existing Anthropic key. Do not inline the value as an env var.

### 6D. SSM Parameter for Brent API Key

The parameter is a SecureString created out-of-band (not by CDK, since the value isn't in source):

```bash
aws ssm put-parameter \
  --name /iran-conflict-map/brent_api_key \
  --value "YOUR_API_NINJAS_OR_OILPRICE_KEY" \
  --type SecureString \
  --region us-east-1
```

In the CDK stack, grant the Sync Lambda read access:

```csharp
IStringParameter brentKeyParam = StringParameter.FromSecureStringParameterAttributes(
    this, "BrentApiKeyParam",
    new SecureStringParameterAttributes
    {
        ParameterName = "/iran-conflict-map/brent_api_key",
        Version = 1,
    });

brentKeyParam.GrantRead(syncFunction);
```

### 6E. No Other New Resources

No new SQS queues, S3 buckets, or EventBridge rules needed.

---

## 7. Historical Backfill

Data goes back to 2026-02-28. Without backfill, the sparkline will be incomplete and the economic tab will be empty for historical dates. Backfill is different for each tier.

### 7A. Tier 1 Backfill — Brent Prices (Straightforward)

For historical dates, EIA is the right source even though it's unsuitable for live syncs. Historical settlement prices are exactly what we want for past dates, and the 1–2 day lag is irrelevant when backfilling records from weeks ago. The near-real-time sources (API Ninjas, Oil Price API) are not designed for date-range historical fetches.

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
2. For each returned `(period, value)` pair, writes a minimal economic record to DynamoDB:
   - `brent_close` populated from EIA settlement price; `brent_fetched_at` set to the backfill run time (not the settlement date — make this obvious in logs).
   - `hormuz_status = "unknown"` (not yet extracted).
   - `oil_export_volume_mbd = null`.
   - `economic_notes = []`.
   - `source_url = ""` (no report URL for these placeholder records).
3. Does NOT overwrite records that already have a non-empty `source_url` (use `ConditionExpression: attribute_not_exists(source_url) OR source_url = :empty`).

This creates placeholder Brent price records for all historical trading days. When a date is later re-synced through the live pipeline, the PutItem overwrites the placeholder with full Brent + Claude data.

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
1. Add the `iran-conflict-map-economic` DynamoDB table + GSI to the CDK stack.
2. Add IAM grants for Sync and API Lambdas.
3. Add SSM parameter reference + grant for Brent API key.
4. Deploy: `cdk deploy`. Verify table exists in console.
5. Manually create SSM parameter: `aws ssm put-parameter --name /iran-conflict-map/brent_api_key --value "..." --type SecureString`.

### Step 2 — Sync Lambda: Brent price call
1. Add `FetchBrentPriceAsync()` to `Function.cs`. Wire into handler after `FetchReportTextAsync`.
2. Log the result. Do not write to DynamoDB yet.
3. Deploy. Trigger manual sync, verify call succeeds and logs a price in CloudWatch.

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

### 9B. Which Commodity API to Use
**Status: Blocking for Step 2.** Choose API Ninjas or Oil Price API (see Section 2). Register for the key — both are free and instant. Store in SSM as `/iran-conflict-map/brent_api_key` before deploying Step 2.

### 9C. Hormuz Status Default Behavior
Current plan: Claude defaults to `"open"` when the report is silent and `"unknown"` when the report explicitly acknowledges uncertainty. Verify this is the right semantic — `"open"` could be misleading if the report simply didn't cover the Strait that day. Alternative: default `"unknown"` always and only set `"open"` when the report explicitly confirms unrestricted transit. This is safer but will result in more `"unknown"` entries.

### 9D. Intraday Price vs. Daily Close
Near-real-time sources return the current intraday quote at the time the Lambda runs (typically ~18:00 ET when CTP-ISW publishes). This is not the official ICE daily settlement price, which is published after market close. For a conflict map, the intraday price at time-of-sync is probably preferable — it reflects the market's current reaction to the day's events rather than yesterday's close. This is the assumed approach. If official settlement prices are needed for any reason, EIA (next-day lag) is the only free option.

### 9E. Brent Fetch Timestamp in UI
The strip shows "Brent $85.23" captured at a specific time. Consider whether to surface the timestamp at all — options:
- Show nothing; just the price (cleanest for a non-trading audience).
- Show "Brent $85.23 · as of 2:30 PM ET" on hover.
- Show nothing unless `brent_fetched_at` is more than 24 hours old (staleness warning only).

### 9F. Sparkline Width and Mobile Layout
200px × 32px was estimated. After implementing, check on mobile (the existing `.topbar` is `position:fixed; top:0`). The indicator strip adds ~28px more vertical space. On small screens this could compress the map. Consider making the strip optional/collapsible, or merging sparkline + strip into a single compact row.

### 9G. Backfill Tier 2 — Cost and Scope
Claude API cost for backfilling ~22 days of reports is negligible. Main constraint is whether old CTP-ISW report pages are still accessible. Verify before investing time in the backfill command. Run `tools test-ctp-fetch` against a Feb 2026 URL to confirm accessibility.

### 9H. Multiple Syncs per Day (Morning Reports)
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
