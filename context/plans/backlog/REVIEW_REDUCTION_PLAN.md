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

When you find a strong match (same location, actor, and event type, distance < 5km):
- Set the update's "id" to the matched event's id
- Route it as an update, not ambiguous

When no match is found, or the match is uncertain:
- Route new events as new
- Route genuinely uncertain cases as ambiguous

Do not call search_strikes for events that are clearly new (first mention, novel location).
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

### Should "all updates go to review" still apply?

Currently every matched update — even a confident one — goes to review. That policy was
adopted to prevent silent data corruption from false-positive proximity matches. With tool
use, Claude is attaching a specific `id` it found by lookup rather than the Processor
guessing via proximity. That's a higher-confidence signal.

**Option A**: Keep the policy. All updates still go to review, but the queue shrinks because
fewer items are classified as `ambiguous` and proximity-match false positives drop.

**Option B**: Let Claude-confirmed updates (with `id` attached via tool lookup) bypass
review and write directly. Only ambiguous items go to review.

Option B would dramatically reduce the queue but requires trusting Claude's lookup result.
Decide when implementation is underway and the tool's accuracy can be assessed.

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
