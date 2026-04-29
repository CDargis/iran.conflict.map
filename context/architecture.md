# Iran Conflict Map — Architecture

## System Components

### Frontend
- `frontend/index.html` — Single-page map app (Leaflet.js, vanilla JS, dark theme)
- `frontend/admin.html` — Admin panel: review queue UI + sync monitor
- Deployed to S3, served via CloudFront
- No build step — direct S3/BucketDeployment

### Lambda Functions (all .NET 8)

| Function | Project | Trigger | Role |
|---|---|---|---|
| Extract | `IranConflictMap.Extract` | EventBridge (S3 ObjectCreated) + 3h schedule | Parse email, resolve report URL, enqueue |
| Sync | `IranConflictMap.Sync` | SQS (report queue) | Fetch report page, call Claude, stamp GUIDs, enqueue |
| Processor | `IranConflictMap.Lambda` | SQS (processor queue) | Write events to DynamoDB, route ambiguous to review |
| API | `IranConflictMap.Api` | API Gateway HTTP API | Serve /api/* endpoints |
| Brent | `IranConflictMap.Brent` | EventBridge cron (0,5,10,15,20 UTC) | Fetch Brent crude spot price, write to DynamoDB |

### Infrastructure (CDK)
- `src/IranConflictMap/IranConflictMapStack.cs` — all AWS resources defined here
- `src/IranConflictMap/PipelineStack.cs` — CodePipeline CI/CD

### Admin CLI
- `src/IranConflictMap.Tools` — reseed, submit-url, drain queues, debug commands

---

## Data Flow

```
[CTP-ISW email]
      |
      v
[SES receipt rule]
      |
      v
[S3: iran-conflict-map-email / inbox/]
      |
      v (EventBridge: S3 ObjectCreated)
[Extract Lambda]
  - Validate sender (criticalthreats@aei.org) + keywords
  - Resolve URL via 3 strategies (canonical slug → fuzzy INI_LIST → body scan)
  - On failure: leave in inbox (3h schedule retries)
      |
      v (SQS FIFO: iran-conflict-map-report.fifo)
[Sync Lambda]
  - Idempotency check (run_id in syncs-v2)
  - HTTP fetch report page → strip HTML → truncate 100KB
  - Call Claude Sonnet 4.6 with extraction prompt
  - Claude returns: { new[], updates[], ambiguous[] }
  - Stamp GUIDs on new events
  - Write syncs-v2 record (status=processing)
  - Move email to processed/ prefix
      |
      v (SQS FIFO: iran-conflict-map-processor.fifo)
[Processor Lambda]
  - "new" events: BatchWriteItem to strikes table (max 25/call, retry unprocessed)
  - "updates": lookup by ID or proximity (Haversine ≤10km, date ±1 day)
      - isReviewApproval=true → apply directly
      - else → send to review queue
  - "ambiguous": send to review queue
  - Update syncs-v2 record (final counts + status)
      |
      +---------> [SQS FIFO: iran-conflict-map-review.fifo]  (ambiguous / matched updates)
      |                  |
      |             [Admin reviews via admin.html]
      |             POST /api/review/resolve
      |               action=new: stamp GUID, enqueue to processor (is_review_approval=true)
      |               action=update: enqueue to processor (is_review_approval=true)
      |               action=discard: delete from queue
      |
      v
[DynamoDB: strikes table]
      |
      v (GET /api/strikes?date=YYYY-MM-DD)
[API Lambda]
      |
      v (CloudFront /api/* behavior → API Gateway)
[Frontend map display]
```

---

## AWS Resources

### DynamoDB Tables
- `strikes` — PK: `id` (String). GSI: `entity-date-index` (entity=String, date=String)
- `syncs-v2` — PK: `report_url`, SK: `run_id`. GSI: `entity-run-index` (entity=String, run_id=String)
- `syncs` — legacy table, no longer written (kept for backwards compat)

### SQS Queues (all FIFO, deduplication enabled)
- `iran-conflict-map-report.fifo` — Extract → Sync
- `iran-conflict-map-processor.fifo` — Sync → Processor
- `iran-conflict-map-dlq.fifo` — dead letters (failed/unresolvable items)
- `iran-conflict-map-review.fifo` — ambiguous items awaiting human review

### S3 Buckets
- `iran-conflict-map-email` — SES email storage (inbox/, processed/, other/)
- `conflictmap.chrisdargis.com` — static website (index.html, admin.html, favicon)

### Other
- API Gateway HTTP API → Lambda proxy
- CloudFront distribution: S3 origin (default) + API Gateway origin (/api/*)
- Route 53 A record → CloudFront ALIAS
- ACM certificate (us-east-1) — auto-validated via DNS
- SES receipt rule set → S3
- EventBridge rules: S3 ObjectCreated → Extract; 3h schedule → Extract; cron(0,5,10,15,20) → Brent
- SSM Parameters: `anthropic_api_key`, `last_synced`, `sync_key`, `brent_api_key`

---

## External APIs

| Service | Purpose | Auth | Tier |
|---|---|---|---|
| Anthropic Claude Sonnet 4.6 | Strike event extraction from report text | API key (SSM) | Paid |
| [oilpriceapi.com](https://docs.oilpriceapi.com/api-reference/) | Brent crude spot price (`/v1/prices/latest`, `/v1/prices/past_month`) | Token (SSM) | Free (200 req/month) |

---

## API Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | /api/strikes | public | Events by date. `?date=YYYY-MM-DD` optional |
| GET | /api/syncs | public | Last 50 sync runs grouped by report URL |
| POST | /api/sync/trigger | X-Sync-Key | Invoke Extract Lambda immediately |
| POST | /api/sync/submit-url | X-Sync-Key | Manually enqueue a report URL |
| GET | /api/review | X-Sync-Key | Peek at review queue (up to 10 messages) |
| POST | /api/review/resolve | X-Sync-Key | Approve (new/update) or discard a review item |

Auth: `X-Sync-Key` header, validated against SSM `/iran-conflict-map/sync_key`.

---

## URL Resolution (Extract Lambda)

Three strategies attempted in order:
1. **Canonical**: Build slug from email date (`iran-update-{month}-{day}-{year}`), look up in
   INI_LIST (fetched from CTP-ISW listing page)
2. **Fuzzy**: Check if any INI_LIST slug contains the date fragment
3. **Body scan**: Regex-extract `criticalthreats.org/analysis/...` URLs from email body/HTML

On failure: email stays in inbox, 3-hour schedule retries.

---

## Review Approval Flow

When the Processor routes an update to the review queue, the admin can:
1. GET /api/review — receive the message with existing record + proposed changes
2. POST /api/review/resolve with action=update (or new/discard)
3. API re-enqueues to processor with `is_review_approval=true`
4. Processor bypasses proximity threshold and applies the update directly

---

## Caching

API Lambda: in-memory dict keyed by date string, 5-minute TTL. Cache cleared on each
/api/strikes or /api/syncs call if TTL expired. CloudFront has no caching on /api/* behavior.
