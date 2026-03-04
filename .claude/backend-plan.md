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

### 2. New Project — `IranConflictMap.Api`
- New ASP.NET Core minimal API project under `src/`
- `Amazon.Lambda.AspNetCoreServer.Hosting` handles the Lambda↔HTTP translation
- Single endpoint to start: `GET /strikes`
  - DynamoDB Scan → return all items as JSON array
- Runs locally as a normal API for easy development/testing
- Adding endpoints later is just adding more `app.MapGet(...)` calls

NuGet packages:
- `Amazon.Lambda.AspNetCoreServer.Hosting`
- `AWSSDK.DynamoDBv2`

### 2b. Existing `IranConflictMap.Lambda` project
- Leave untouched for now
- Will become the hydration/ingest function (EventBridge triggered)
- Implementation deferred until data source is defined

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

### 5. Seed on Deploy
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
| `src/IranConflictMap/IranConflictMapStack.cs` | Add DynamoDB, Lambda (GetStrikes), API Gateway, CF behavior |
| `src/IranConflictMap.Api/` | New ASP.NET Core minimal API project |
| `src/IranConflictMap.Lambda/` | Untouched — reserved for ingest hydration |
| `frontend/index.html` | Update STRIKES_URL to `/api/strikes` |

---

## Open Questions
- Ingest Lambda: what data source will it hit? (news API, RSS, manual input?) — deferred
