# Backend Plan — Iran Conflict Map

## Goal
Replace static `strikes.json` with a real API backed by DynamoDB.
Cost target: effectively $0 (free tier).

---

## Architecture

```
EventBridge (scheduled rule)
  └→ Ingest Lambda  ─────────────────→ DynamoDB (strikes table)
                                              ↑
Frontend (CloudFront)                         │
  └→ /api/strikes                             │
       └→ CloudFront /api/* behavior          │
            └→ API Gateway (HTTP API)         │
                 └→ GetStrikes Lambda ────────┘
```

---

## Components

### 1. DynamoDB Table
- Table name: `strikes`
- Billing: PAY_PER_REQUEST (no minimum cost)
- Partition key: `id` (string)
- No sort key needed
- RemovalPolicy: RETAIN (don't lose data on stack destroy)

### 2. Lambda Project — two handlers in `IranConflictMap.Lambda`
- **`GetStrikes`** — triggered by API Gateway
  - DynamoDB Scan → return all items as JSON array
  - Should return CORS headers (or let CloudFront handle it — see #4)
- **`IngestStrikes`** — triggered by EventBridge (stub for now)
  - Signature wired up, implementation TBD
  - Will PutItem for each new strike found
  - Deduplication is free: PutItem with existing `id` just overwrites

Add NuGet packages to Lambda project:
- `Amazon.Lambda.APIGatewayEvents`
- `AWSSDK.DynamoDBv2`

### 3. API Gateway — HTTP API
- Single route: `GET /strikes`
- Lambda integration → GetStrikes handler
- No auth (public read)
- Default stage: `$default`

### 4. CloudFront — add /api/* behavior
- New origin: API Gateway HTTP API endpoint
- Cache behavior: path `/api/*`
  - CachePolicy: CACHING_DISABLED (strikes data should be fresh)
  - AllowedMethods: GET_HEAD
  - ViewerProtocolPolicy: REDIRECT_TO_HTTPS
- This keeps everything on `conflictmap.chrisdargis.com` — no CORS needed

### 5. EventBridge Rule
- Schedule: rate(1 hour) or cron — TBD when automation is built out
- Target: IngestStrikes Lambda
- Start disabled until implementation is ready

### 6. Seed on Deploy
- After table is created, seed from current `strikes.json`
- Options:
  a. Custom CDK resource (Lambda-backed) that runs once
  b. Manual `aws dynamodb batch-write-item` as a one-time step
- Recommendation: (b) keep it simple, do it manually once

### 7. Frontend change
- Update `STRIKES_URL` from `'strikes.json'` to `'/api/strikes'`
- `strikes.json` can remain in S3 as a fallback during transition,
  then be removed once API is confirmed working

---

## File Changes

| File | Change |
|---|---|
| `src/IranConflictMap/IranConflictMapStack.cs` | Add DynamoDB, Lambda (both handlers), API Gateway, CF behavior, EventBridge |
| `src/IranConflictMap.Lambda/Function.cs` | Replace stub with GetStrikes + IngestStrikes handlers |
| `src/IranConflictMap.Lambda/IranConflictMap.Lambda.csproj` | Add DynamoDB + API Gateway Events NuGet packages |
| `frontend/index.html` | Update STRIKES_URL to `/api/strikes` |

---

## Open Questions
- Ingest Lambda: what data source will it hit? (news API, RSS, manual input?)
- Should IngestStrikes be a separate Lambda function or a second handler in the same project?
- Do we want a `GET /strikes?actor=US` filter at the API layer, or keep client-side filtering?
