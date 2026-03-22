# Iran Conflict Map — Schema

All DynamoDB items are stored in wire format (`{ "S": "..." }`, `{ "N": "..." }`, etc.).
TypeScript interfaces below represent the logical shape after deserialization.

---

## strikes table

PK: `id` (String)
GSI: `entity-date-index` — PK: `entity`, SK: `date`

```typescript
interface StrikeEvent {
  // Keys
  id: string;               // GUID (stamped by Sync Lambda before enqueue)
  entity: "strike";         // Always "strike" — required for GSI

  // Event identity
  date: string;             // YYYY-MM-DD — date the event occurred (not report date)
  title: string;            // Short headline
  location: string;         // Human-readable place name
  lat: number;              // Decimal degrees
  lng: number;              // Decimal degrees

  // Classification
  type: "strike" | "drone" | "naval" | "missile";
  target_type: "military" | "maritime" | "nuclear" | "command" | "civilian";
  actor: string;            // Free text: "Israel Defense Forces", "US", "Iran", "Houthi", etc.
  severity: "low" | "medium" | "high" | "critical";

  // Content
  description: string;      // 1–3 sentence factual summary; immutable after creation
  casualties: {
    confirmed: number;
    estimated: number;
  };
  source_url?: string;      // CTP-ISW report URL; same for all events from one report
  citations?: string[];     // Resolved footnote URLs directly associated with this event
  notes?: string[];         // Append-only log; new confirmed details from later reports

  // Flags
  disputed?: boolean;       // Only present if event is contested/unverified; omitted otherwise

  // Audit
  created_at: string;       // ISO 8601, stamped by Processor Lambda
  updated_at: string;       // ISO 8601, updated on every change
  update_log?: Array<{      // Audit trail of field changes
    at: string;             // ISO 8601
    fields: string[];
    source_url: string;
  }>;
}
```

### Severity guidelines (from extraction prompt)
- `low` — 0 casualties, warning shots, disputed/intercepted attacks
- `medium` — 1–10 casualties, localized damage
- `high` — 10–50 casualties, major infrastructure/military target
- `critical` — 50+ casualties, nuclear facility, decapitation, strategic escalation

---

## syncs-v2 table

PK: `report_url` (String), SK: `run_id` (String, ISO 8601 timestamp)
GSI: `entity-run-index` — PK: `entity`, SK: `run_id`

```typescript
interface SyncRecord {
  // Keys
  report_url: string;       // Normalized CTP-ISW report URL (PK)
  run_id: string;           // ISO 8601 timestamp, stable per enqueue (SK)
  entity: "sync";           // Always "sync" — required for GSI

  // Status
  status:
    | "processing"          // Sync Lambda wrote this; Processor hasn't updated yet
    | "success"             // All items processed, no dead letters
    | "partial"             // Some items dead-lettered
    | "fetch_error"         // Could not fetch report page
    | "claude_error"        // Claude API call failed
    | "error";              // Other failure

  // Counts (written by Processor Lambda; zero until Processor runs)
  new_event_count: number;
  update_count: number;
  dead_letter_count: number;
  review_count: number;

  // Metadata
  url_strategy?: "canonical" | "ini_list" | "body_scan" | "manual";
  error_message?: string;   // Truncated to 1000 chars
}
```

---

## Review Queue message shape

Messages on `iran-conflict-map-review.fifo`. Not a DynamoDB table — SQS message body.

```typescript
interface ReviewMessage {
  note: string;             // Human-readable explanation of why this is in review
  existing_record?: object; // Simplified existing DynamoDB item (if update/ambiguous)
  as_new?: object;          // Proposed new event (wire format)
  as_update?: object;       // Proposed update (wire format)
  source_url?: string;
  sync_id?: string;         // run_id of the sync that produced this
  is_review_approval: boolean; // Always false when first queued from Processor
}
```

---

## SyncEnvelope (SQS message body, internal pipeline)

Passed from Sync Lambda → Processor Lambda on `iran-conflict-map-processor.fifo`.

```typescript
interface SyncEnvelope {
  source_url: string;
  synced_at: string;            // run_id / ISO timestamp
  new: object[];                // DynamoDB wire format items
  updates: object[];            // DynamoDB wire format update records
  ambiguous: object[];          // DynamoDB wire format items + review context
  is_review_approval: boolean;  // If true, Processor bypasses proximity threshold
}
```

---

## Planned (not yet implemented): morning reports

From `plans/active/MORNING_REPORTS_PLAN.md` — schema additions proposed but not deployed:

```typescript
// Additional fields on StrikeEvent for morning report support
interface StrikeEventMorningFields {
  preliminary?: true;   // Sparse GSI — only present on morning report events; cleared on evening resolution
  source?: "morning";   // Immutable origin label; absent on evening-only events
}

// Proposed new GSI on strikes table
// Name: preliminary-date-index
// PK: preliminary (Boolean), SK: date (String)

// Proposed audit row (in strikes table, different entity value)
interface AuditRow {
  entity: "audit";              // Identifies row type
  id: string;                   // "{event_id}#{timestamp_iso}"
  event_id: string;
  at: string;                   // ISO 8601
  source: "morning" | "evening" | "review" | "manual";
  fields: string[];             // Which fields changed
  values_before?: object;       // Omitted on insert
  values_after: object;
}
```
