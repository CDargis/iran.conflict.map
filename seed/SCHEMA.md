# DynamoDB Schema — `strikes` table

## Table config
- **Table name:** `strikes`
- **Partition key:** `id` (String)
- **Billing:** PAY_PER_REQUEST

## Item fields

| Field         | DynamoDB type | Example value              | Notes                                      |
|---------------|---------------|----------------------------|--------------------------------------------|
| `id`          | S (String)    | `"8"`                      | Partition key, unique per event            |
| `entity`      | S (String)    | `"strike"`                 | Always `"strike"` — GSI partition key      |
| `date`        | S (String)    | `"2026-03-01"`             | ISO 8601 date (YYYY-MM-DD)                 |
| `title`       | S (String)    | `"US CENTCOM Strike Package"` | Short display title                     |
| `location`    | S (String)    | `"Deir ez-Zor, Syria"`     | Human-readable place name                  |
| `lat`         | N (Number)    | `35.33`                    | Latitude (decimal degrees)                 |
| `lng`         | N (Number)    | `40.14`                    | Longitude (decimal degrees)                |
| `type`        | S (String)    | `"strike"`                 | Event type — see allowed values below      |
| `target_type` | S (String)    | `"command"`                | What was targeted — see allowed values below |
| `actor`       | S (String)    | `"US"`                     | Attacking party — see allowed values below |
| `description` | S (String)    | `"Multi-aircraft strike…"` | 1–3 sentence summary                       |
| `severity`    | S (String)    | `"high"`                       | Event severity — see allowed values below |
| `casualties`  | M (Map)       | `{ confirmed: N, estimated: N }` | Both sub-fields are Numbers          |
| `disputed`    | BOOL          | `true`                           | Optional. Omit or set false if not disputed |

## Allowed enum values

### `type`
- `strike` — air/missile strike
- `drone` — drone attack or swarm
- `naval` — naval engagement or intercept
- `missile` — ballistic/cruise missile launch

### `target_type`
- `military` — military base or personnel
- `maritime` — ships or port infrastructure
- `nuclear` — nuclear facility or adjacent site
- `command` — command-and-control node
- `civilian` — civilian infrastructure

### `severity`
- `low` — minor incident, no/minimal casualties, warning shots, contested intercepts
- `medium` — limited engagement, 1–10 casualties, localized damage
- `high` — significant strike, 10–50 casualties, major infrastructure or military target
- `critical` — mass casualty event (50+), nuclear facility, decapitation strike, or strategic escalation

### `actor`
- `US`
- `Israel`
- `Iran`

## Seed file format (for `batch-write-item`)

```json
{
  "strikes": [
    {
      "PutRequest": {
        "Item": {
          "id":          { "S": "9" },
          "entity":      { "S": "strike" },
          "date":        { "S": "2026-03-04" },
          "title":       { "S": "Example Event Title" },
          "location":    { "S": "City, Country" },
          "lat":         { "N": "33.00" },
          "lng":         { "N": "44.00" },
          "type":        { "S": "strike" },
          "target_type": { "S": "military" },
          "actor":       { "S": "US" },
          "description": { "S": "Brief description of the event." },
          "casualties":  { "M": { "confirmed": { "N": "0" }, "estimated": { "N": "0" } } },
          "disputed":    { "BOOL": false }
        }
      }
    }
  ]
}
```

## CLI seed command

```bash
aws dynamodb batch-write-item --request-items file://seed/strikes-seed.json
```

> **Note:** DynamoDB batch-write-item supports max 25 items per call. Split the file if needed.

## Prompt template for Claude

Paste this at the top of a new Claude conversation:

---

I'm building an Iran/Middle East conflict map. I need you to generate DynamoDB seed data for the `strikes` table.

**Schema:**
- `id` (String) — unique numeric string, increment from the last ID I give you
- `entity` (String) — always `"strike"` (used as GSI partition key)
- `date` (String) — ISO 8601, YYYY-MM-DD
- `title` (String) — short event title
- `location` (String) — human-readable place name
- `lat` / `lng` (Number) — decimal degrees
- `type` (String) — one of: `strike`, `drone`, `naval`, `missile`
- `target_type` (String) — one of: `military`, `maritime`, `nuclear`, `command`, `civilian`
- `actor` (String) — one of: `US`, `Israel`, `Iran`, `Houthi/Iran`
- `description` (String) — 1–3 sentence factual summary
- `casualties` (Map) — `{ confirmed: N, estimated: N }`
- `disputed` (Boolean, optional) — set to `true` if the event is contested, unverified, or denied by a party

**Output format** (DynamoDB batch-write-item JSON):
```json
{
  "strikes": [
    {
      "PutRequest": {
        "Item": {
          "id":          { "S": "..." },
          "entity":      { "S": "strike" },
          "date":        { "S": "YYYY-MM-DD" },
          "title":       { "S": "..." },
          "location":    { "S": "..." },
          "lat":         { "N": "..." },
          "lng":         { "N": "..." },
          "type":        { "S": "..." },
          "target_type": { "S": "..." },
          "actor":       { "S": "..." },
          "description": { "S": "..." },
          "casualties":  { "M": { "confirmed": { "N": "0" }, "estimated": { "N": "0" } } },
          "disputed":    { "BOOL": false }
        }
      }
    }
  ]
}
```

The last used ID is **[X]**. Please generate [N] new events starting from ID [X+1].

For `severity`, use these guidelines:
- `low` — minor incident, 0 casualties, warning shots, disputed/intercepted attacks
- `medium` — limited engagement, 1–10 casualties, localized damage
- `high` — significant strike, 10–50 casualties, major infrastructure or military target
- `critical` — mass casualty event (50+ estimated), nuclear facility strike, decapitation strike, or major strategic escalation

---
