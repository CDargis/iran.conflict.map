# Review Queue Reduction — Tool Use Plan

## Problem

Every potential update to an existing strike event goes to the review queue. The Processor
Lambda resolves updates by proximity matching (Haversine, 10km, ±1 day GSI query) — but
since Claude doesn't know what's already in the database when it reads a report, it can't
attach a known `id` to updates. Anything matched by proximity goes to review because a human
needs to confirm the AI's guess.

The result: even confident, obvious updates (same location, same actor, mentioned by name)
land in the review queue alongside genuinely ambiguous ones.

---

## Proposed Fix: Give Claude a Lookup Tool

Claude's API supports **tool use** (function calling). During report extraction, the Sync
Lambda can expose a tool that lets Claude query the existing strikes database. Claude calls
the tool when it encounters what looks like an update, gets back the matching event(s), and
uses that to:

1. Attach the correct `id` to the update payload — eliminating the need for proximity matching
2. Make a confident new-vs-update decision at extraction time
3. Flag only genuinely ambiguous cases for human review

---

## Tool Design

### `search_strikes`

The only tool Claude needs. Takes a rough location and date range, returns matching events.

```typescript
// Tool input (Claude provides these)
{
  lat: number;         // approximate latitude from report
  lng: number;         // approximate longitude from report
  date: string;        // YYYY-MM-DD — the date mentioned in the report
  description?: string; // optional — Claude's brief description of the event
}

// Tool output (Sync Lambda returns this)
{
  matches: Array<{
    id: string;
    date: string;
    title: string;
    location: string;
    lat: number;
    lng: number;
    actor: string;
    type: string;
    description: string;
    distance_km: number;  // computed by the Lambda from the input coords
  }>;
}
```

The Lambda handles the DynamoDB query (same entity-date GSI, ±1 day window) and computes
Haversine distance for each candidate, returning the top N sorted by distance.

---

## Sync Lambda Changes

### Tool call loop

Claude API with tools requires a multi-turn loop:

```
1. Send message with tools defined + report text
2. If response has tool_use blocks → execute each, collect results
3. Send tool results back to Claude
4. Repeat until response has no tool_use blocks
5. Extract final text response and parse JSON as before
```

This replaces the current single-shot `Messages.Create()` call. The loop typically completes
in 2 turns (one tool call batch, one final response) but must handle N turns.

### Tool execution in Lambda

When Claude calls `search_strikes`, the Lambda:
1. Queries the strikes table GSI (`entity-date-index`) for `date ± 1 day`
2. Computes Haversine distance from each result to the input coords
3. Returns top 5 matches sorted by distance, including `distance_km`

Claude then decides: if a match is close enough and matches the description, it attaches that
`id` to the update. If nothing matches well, it marks it as new or explicitly flags ambiguity.

### System prompt changes

Add a section to the system prompt explaining the tool:

```
You have access to a search_strikes tool. Use it when you encounter an event that sounds like
it may be an update to something already in the database — a follow-up strike on the same
target, additional casualties reported for a known event, etc.

Date differences of ±1 day between the current report and a candidate are common due to
timezone differences — do not treat a one-day gap as disqualifying, but do not reach for
a match that is off by a day unless the descriptions clearly align.

When evaluating candidates, weight signals in this order:
1. Description similarity — does it describe the same action at the same target?
2. Location — same place name or close coordinates
3. Date — same or ±1 day
4. Type — strike, missile, drone, etc.
5. Actor — least reliable; attribution often changes between reports (e.g. "Iran" in an
   early report later clarified as IRGC or a specific militia). Do not let actor mismatch
   disqualify an otherwise strong match.

When you find a strong match:
- Place it in "tool_updates" with the matched event's "id" and only the changed fields
- Do NOT place it in "updates" or "ambiguous"

When the tool returns no good candidates: place in "new".
When genuinely uncertain whether new or update even after a lookup: place in "ambiguous".
When clearly a new event (first mention, novel location): place in "new" without calling the tool.

Do not use the "updates" array at all — it is being retired. Every event is either
"new", "tool_updates" (matched via tool), or "ambiguous".
```

---

## What Still Goes to Review

Tool use reduces the queue — it doesn't eliminate it. Cases that should still go to review:
- Claude explicitly marks something `ambiguous` even after a lookup
- Two close matches returned and Claude can't distinguish them
- Update with an `id` attached but a human should verify the field changes
  (this is the existing "all updates go to review" policy — may be worth revisiting
  separately once tool use is in place and the queue is smaller)

---

## Open Questions

### Rollout approach — staged review

Claude puts confident tool-lookup matches in a new `tool_updates` array (separate from
`ambiguous`). On initial rollout, `tool_updates` go straight to the review queue like
everything else — but the review item will already have the matched `id` attached, making
it fast to verify. After a few days of manually checking accuracy, if Claude's matches are
good, flip `tool_updates` to bypass review and write directly.

This avoids a big-bang trust decision. The output format change is minimal:

```json
{
  "new":          [ ... ],
  "tool_updates": [ { "id": "...", "changes": { ... } } ],  // Claude found match via tool
  "ambiguous":    [ ... ]
}
```

Everything currently going into `updates` will now resolve to one of:
- `tool_updates` — tool found a match, ID attached
- `new` — tool found no match; treat as a new event

**Phase 1 (initial rollout):** Keep `updates` and proximity matching in the Processor as a
safety net. Claude should not use `updates` anymore, but the Processor still handles it in
case something slips through.

**Phase 2 (once tool accuracy is validated):** Remove `updates` support and the Processor's
proximity matching logic (Haversine, 10km, ±1 day GSI query) — it only exists to resolve
`updates` lookups and will no longer be needed.

Processor Lambda routes `tool_updates` to review initially. Later: write directly and skip review.

### Matching strategy — ±1 day and location variance

The tool should query ±1 day unconditionally. The one-day variance is likely a timezone
issue — events near midnight in Iraq/Iran/Israel (UTC+2 to UTC+3) can land on different
calendar dates depending on how the report and the sync handle them. No point trying to
fix the root cause; just widen the window.

Location variance is real — the same target described differently across reports can produce
coordinates 5–15km apart. The current Processor proximity threshold (10km) may be too tight.
The tool should return all candidates within a generous radius (say 25km) and let Claude
decide based on semantic similarity — title, location string, actor, type, description.
Claude reading both descriptions is far more reliable than any distance threshold alone.

### Actor name matching

When Claude returns an actor in a tool lookup result or update payload, it needs to match
existing records. This was initially a concern (fuzzy matching on actor strings), but was
resolved by standardizing the actor field to a canonical list:

- Extraction prompt now instructs Claude to use canonical actor names
- A `normalize-actors` CLI command backfilled all historical records
- Going forward, actor values will be consistent — no fuzzy matching needed

### Latency

Tool calls add one round trip to the Claude API per extraction. Current sync is already
async (EventBridge → Lambda chain) so latency is not user-facing. Not a concern.

### Cost

More tokens per extraction (tool definitions + tool results in context). Marginal at one
report/day. Not a concern.

---

## Implementation Order

1. Add `search_strikes` tool execution logic to Sync Lambda (DynamoDB query + Haversine)
2. Switch Claude API call from single-shot to tool-call loop
3. Add tool definition and system prompt section
4. Test against a few historical reports manually
5. Decide on Option A vs B and configure accordingly
6. Monitor review queue volume after rollout
