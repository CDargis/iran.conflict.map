using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Sync;

public class Function
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect        = true,
        MaxAutomaticRedirections = 10
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan  // timeout via Lambda CancellationToken instead
    };

    private readonly IAmazonDynamoDB                  _dynamo;
    private readonly IAmazonSimpleSystemsManagement   _ssm;
    private readonly IAmazonSQS                       _sqs;
    private readonly IAmazonS3                        _s3;

    private static readonly string StrikesTable      = Env("STRIKES_TABLE",       "strikes");
    private static readonly string SyncsTable        = Env("SYNCS_TABLE",         "syncs");
    private static readonly string SignalsTable      = Env("SIGNALS_TABLE_NAME",  "iran-conflict-map-economic-signals");
    private static readonly string SsmPrefix         = Env("SSM_PREFIX",          "/iran-conflict-map");
    private static readonly string ProcessorQueueUrl = Env("PROCESSOR_QUEUE_URL", "");
    private static readonly string CleanupQueueUrl   = Env("CLEANUP_QUEUE_URL",   "");
    private static readonly string EmailBucket       = Env("EMAIL_BUCKET",        "");

    // ── System prompt ─────────────────────────────────────────────────────────
    private const string SystemPrompt = """
        I'm building an Iran/Middle East conflict map. I need you to process a CTP-ISW Iran update report and extract strike events into structured DynamoDB data.
        Schema:

        id — do not include; assigned automatically
        entity (String) — always "strike" (GSI partition key, required)
        date (String) — ISO 8601, YYYY-MM-DD
        title (String) — short event title
        location (String) — human-readable place name
        lat / lng (Number) — decimal degrees
        type (String) — one of: strike, drone, naval, missile
        target_type (String) — one of: military, maritime, nuclear, command, civilian
        actor (String) — the attacking party. Use the canonical name from this list whenever possible:
          "US Central Command", "Israel Defense Forces", "US-Israel Combined Force",
          "Islamic Revolutionary Guard Corps", "Islamic Republic of Iran Army",
          "Iran", "Lebanese Hezbollah", "Iranian-backed Iraqi Militias", "Houthi Movement", "Unknown".
          Use "Iran" when it is clearly Iranian military action but IRGC vs. conventional army cannot be determined.
          Use "Iranian-backed Iraqi Militias" for all Iraqi proxy groups regardless of specific faction name.
          Only use a value outside this list if the actor is genuinely not covered by any of the above.
        severity (String) — one of: low, medium, high, critical
        description (String) — 1–3 sentence factual summary (new events only; never include in changes)
        casualties (Map) — { confirmed: N, estimated: N }
        notes (String, optional) — additive context confirmed by subsequent reports (e.g. "CENTCOM confirmed on March 16 that..."); append-only, do not repeat information already in description; omit on new events
        source_url — do not include; stamped automatically from the report URL
        citations (List of Strings, optional) — URLs of primary sources cited inline in this event's topline paragraph; resolve each footnote marker (e.g. [i], [ii], [xv]) to its full URL from the footnote block at the bottom of the report; only include footnotes directly associated with this event's paragraph; omit if none
        disputed (Boolean, optional) — set to true if the event is contested, unverified, or denied by a party; omit if not disputed

        Severity guidelines:
        low — minor incident, 0 casualties, warning shots, disputed/intercepted attacks
        medium — limited engagement, 1–10 casualties, localized damage
        high — significant strike, 10–50 casualties, major infrastructure or military target
        critical — mass casualty event (50+ estimated), nuclear facility strike, decapitation strike, or major strategic escalation

        Granularity rule: Generate one event per distinct operation or topline paragraph. Do not log individual munitions within a barrage as separate events. A single coordinated wave of strikes on the same target type in the same location on the same date = one event.
        Citation mapping rule: The report contains inline footnote markers (e.g. [i], [ii], [xv]) within each topline paragraph, and a corresponding footnote block at the bottom resolving each marker to a URL. For each event, collect only the footnote markers within that event's paragraph, resolve them to their URLs, and include those in citations. Omit the field entirely if no footnotes are mappable.
        New vs update rule: Classify each extracted event as:
        new — not previously reported; do not include id, it is assigned automatically
        ambiguous — you called search_strikes and cannot confidently determine whether this is new or an update to an existing record; include a "note" explaining the uncertainty, a complete "as_new" item (same PutRequest/Item wire format as new events), and an "as_update" payload with the best candidate's "id" and only the changed fields in "changes" (DynamoDB wire format — never include description; use notes instead)

        Tool use: You have access to a search_strikes tool. You MUST use it before classifying any event as an update.

        For any event dated before the report's own publication date: you MUST call search_strikes before classifying it as new. Events the report dates to a prior day are frequently follow-ups or additional detail on something already recorded.
        For any event dated on the report's publication date that sounds like an update — a follow-up strike on the same target, additional casualties confirmed, new details about a prior operation — also call the tool before classifying it.

        When evaluating candidates returned by the tool, weight signals in this order:
        1. Description similarity — does it describe the same action at the same target?
        2. Location — same place name or close coordinates
        3. Date — same or ±1 day (date differences of ±1 day are common due to timezone offsets — do not treat a one-day gap as disqualifying, but do not reach for a match that is off by a day unless the descriptions clearly align)
        4. Type — strike, missile, drone, etc.
        5. Actor — least reliable; attribution often changes between reports. Do not let actor mismatch disqualify an otherwise strong match.

        When you find a strong match via the tool and this report adds meaningful new detail (casualties, confirmation, additional context): place it in "tool_updates" with the matched event's "id" and only the changed fields (DynamoDB wire format — never include description; use notes instead).
        When you find a strong match via the tool and this report adds no new detail: omit the event entirely — it is already recorded.
        When the tool returns no good candidates: place in "new".
        When genuinely uncertain even after a lookup: place in "ambiguous".
        When clearly a new event dated on the report's publication date (first mention, novel location): place in "new" without calling the tool.

        Output format — return a single JSON object and NOTHING ELSE. No explanation, no preamble, no text after the closing brace. The response must begin with '{' and end with '}'. Any observations, caveats, or commentary must go inside the "sync_notes" array, not outside the JSON.
        {
          "new": [ { "PutRequest": { "Item": { ... DynamoDB wire format ... } } }, ... ],
          "tool_updates": [ { "id": "...", "changes": { ... DynamoDB wire format — changed fields only, never include description ... } }, ... ],
          "ambiguous": [ { "note": "...", "as_new": { "PutRequest": { "Item": { ... DynamoDB wire format ... } } }, "as_update": { "id": "...", "changes": { ... DynamoDB wire format — changed fields only, never include description ... } } }, ... ],
          "sync_notes": [ "...", ... ]
        }
        Only include "disputed" when genuinely contested. Only include "citations" when footnotes are mappable. Only include fields in "changes" that are actually changing. Include "sync_notes" only when there is something worth noting (e.g. ambiguous sourcing, unusual report, low-confidence classifications, data gaps). Omit the field entirely if there are no notes.

        Additionally, extract economic signals from the report and include an "economic" object in your response:
        {
          "date": "YYYY-MM-DD",
          "hormuz_status": "no_alert",
          "economic_notes": ["..."]
        }

        Field definitions:
        - date — YYYY-MM-DD of the report
        - hormuz_status — "no_alert" if the report is silent on Hormuz; "open" if the report explicitly confirms normal/open passage at the Strait; "restricted" if it explicitly describes interference, seizures, mining, blockade activity, or legislative/military actions asserting control over the Strait; "closed" if it describes a full blockade or closure
        - economic_notes — flat array of concise complete sentences, one per distinct signal. Include: active or newly-announced sanctions and designations, OFAC/Treasury actions, energy infrastructure damage or threats, oil/gas price mentions tied to conflict activity, shipping insurance or Lloyd's notices, export bans or waivers, financial system impacts (SWIFT, correspondent banking). Empty array [] if nothing qualifies.

        Always include the "economic" object — even if hormuz_status is "no_alert" and economic_notes is empty.
        """;

    public Function()
    {
        _dynamo = new AmazonDynamoDBClient();
        _ssm    = new AmazonSimpleSystemsManagementClient();
        _sqs    = new AmazonSQSClient();
        _s3     = new AmazonS3Client();
    }

    public Function(IAmazonDynamoDB dynamo, IAmazonSimpleSystemsManagement ssm, IAmazonSQS sqs, IAmazonS3 s3)
    {
        _dynamo = dynamo;
        _ssm    = ssm;
        _sqs    = sqs;
        _s3     = s3;
    }

    // ── SQS trigger ───────────────────────────────────────────────────────────
    // Message body: { "url": "https://...", "email_key": "inbox/..." (optional) }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            await ProcessMessage(record.Body, record.MessageId, context);
        }
    }

    private async Task ProcessMessage(string messageBody, string messageId, ILambdaContext context)
    {
        var msg = JsonSerializer.Deserialize<ReportMessage>(messageBody)
            ?? throw new Exception($"Could not deserialize SQS message: {messageId}");

        var reportUrl   = NormalizeUrl(msg.Url);
        var emailKey    = msg.EmailKey;                                    // null for manual submissions
        var urlStrategy = msg.UrlStrategy;                                 // null for manual submissions
        var runId       = msg.RunId ?? DateTime.UtcNow.ToString("o");     // stable across SQS retries

        context.Logger.LogLine($"[sync] run started: {runId}");
        context.Logger.LogLine($"[sync] url={reportUrl}  email_key={emailKey ?? "(none)"}  url_strategy={urlStrategy ?? "(manual)"}");

        // Cancel a few seconds before Lambda times out so we can log and write the sync record cleanly
        using var cts = new System.Threading.CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(10));
        System.Threading.CancellationToken token = cts.Token;

        // ── Idempotency check — skip if this run_id was already processed ─────
        var existingSync = await _dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = SyncsTable,
            Key       = new Dictionary<string, AttributeValue>
            {
                ["report_url"] = new() { S = reportUrl },
                ["run_id"]     = new() { S = runId }
            }
        });
        if (existingSync.Item != null && existingSync.Item.Count > 0)
        {
            context.Logger.LogLine($"[sync] run_id={runId} already has a sync record (status={existingSync.Item.GetValueOrDefault("status")?.S ?? "unknown"}) — skipping to prevent duplicate writes");
            return;
        }

        try
        {
            var (anthropicKey, lastSynced) = await ReadSsmParams();
            context.Logger.LogLine($"[sync] last_synced={lastSynced}");

            // ── 1. Fetch report page ──────────────────────────────────────────
            var reportText = await FetchReportPage(reportUrl, context, token);
            if (string.IsNullOrWhiteSpace(reportText))
            {
                context.Logger.LogLine("[sync] report page returned empty content");
                await WriteSyncRecord(runId, "fetch_error", 0, 0, 0,
                    $"Empty or failed response fetching {reportUrl}", reportUrl, urlStrategy);
                return;
            }

            // ── 2. Call Claude ────────────────────────────────────────────────
            context.Logger.LogLine("[sync] calling Claude");

            var claudeJson = await CallClaude(reportText, reportUrl, lastSynced, anthropicKey, context, token);
            if (claudeJson == null)
            {
                context.Logger.LogLine("[sync] Claude returned no usable response");
                await WriteSyncRecord(runId, "claude_error", 0, 0, 0,
                    "Claude returned empty or malformed JSON — possible JS-rendered content or wrong page", reportUrl, urlStrategy);
                return;
            }

            // ── 3. Stamp GUIDs on new events ──────────────────────────────────
            var claudeDoc        = JsonDocument.Parse(claudeJson).RootElement;
            JsonElement? newWithIds = null;
            if (claudeDoc.TryGetProperty("new", out var newArr) && newArr.ValueKind == JsonValueKind.Array)
            {
                using var ms     = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(ms);
                writer.WriteStartArray();
                foreach (JsonElement evt in newArr.EnumerateArray())
                {
                    string newId = Guid.NewGuid().ToString();
                    writer.WriteStartObject();
                    writer.WritePropertyName("PutRequest");
                    writer.WriteStartObject();
                    writer.WritePropertyName("Item");
                    writer.WriteStartObject();
                    writer.WritePropertyName("id");
                    writer.WriteStartObject(); writer.WriteString("S", newId); writer.WriteEndObject();
                    if (evt.TryGetProperty("PutRequest", out JsonElement pr) && pr.TryGetProperty("Item", out JsonElement item))
                    {
                        foreach (JsonProperty prop in item.EnumerateObject())
                        {
                            if (prop.Name == "id") continue; // discard any id Claude may have included
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject(); // Item
                    writer.WriteEndObject(); // PutRequest
                    writer.WriteEndObject(); // event
                    context.Logger.LogLine($"[sync] stamped id={newId} on new event");
                }
                writer.WriteEndArray();
                writer.Flush();
                newWithIds = JsonDocument.Parse(ms.ToArray()).RootElement;
            }

            // ── 4. Push to processor SQS ──────────────────────────────────────
            // runId is used as synced_at so the Processor can UpdateItem the same record
            List<string>? syncNotes = null;
            if (claudeDoc.TryGetProperty("sync_notes", out var notesArr) && notesArr.ValueKind == JsonValueKind.Array)
            {
                syncNotes = notesArr.EnumerateArray()
                    .Where(n => n.ValueKind == JsonValueKind.String)
                    .Select(n => n.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (syncNotes.Count == 0) syncNotes = null;
            }

            var envelope = JsonSerializer.Serialize(new
            {
                source_url   = reportUrl,
                synced_at    = runId,
                @new         = newWithIds,
                tool_updates = claudeDoc.TryGetProperty("tool_updates", out var tu) ? tu : (JsonElement?)null,
                ambiguous    = claudeDoc.TryGetProperty("ambiguous",    out var a)  ? a  : (JsonElement?)null,
                sync_notes   = syncNotes
            });

            // Write initial record before pushing — Processor will update counts/status
            await WriteSyncRecord(runId, "processing", 0, 0, 0, null, reportUrl, urlStrategy, syncNotes);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl       = ProcessorQueueUrl,
                MessageBody    = envelope,
                MessageGroupId = "sync"
            });
            context.Logger.LogLine("[sync] Claude response pushed to processor queue");

            // ── 4b. Write economic signals ────────────────────────────────────
            if (claudeDoc.TryGetProperty("economic", out JsonElement economic) && economic.ValueKind == JsonValueKind.Object)
            {
                await WriteEconomicSignals(economic, reportUrl, context, token);
            }

            // ── 4d. Notify cleanup queue — discard stale review items from prior runs ──
            if (!string.IsNullOrEmpty(CleanupQueueUrl))
            {
                string cleanupMessage = System.Text.Json.JsonSerializer.Serialize(new
                {
                    source_url      = reportUrl,
                    current_run_id  = runId
                });
                await _sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl       = CleanupQueueUrl,
                    MessageBody    = cleanupMessage,
                    MessageGroupId = "cleanup"
                });
                context.Logger.LogLine("[sync] cleanup notification queued");
            }

            // ── 5. Move email to processed (only if we have an email key) ─────
            if (!string.IsNullOrEmpty(emailKey))
                await MoveS3Object(emailKey, "processed/" + emailKey["inbox/".Length..], context);

            await UpdateSsmParam($"{SsmPrefix}/last_synced", DateTime.UtcNow.ToString("yyyy-MM-dd"));

            context.Logger.LogLine("[sync] done — pushed to processor queue");
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"[sync] unhandled error: {ex}");
            await WriteSyncRecord(runId, "error", 0, 0, 0,
                ex.Message[..Math.Min(ex.Message.Length, 1000)], reportUrl, urlStrategy);
            throw;  // re-throw so SQS retries
        }
    }

    // ── Report fetch ──────────────────────────────────────────────────────────

    private async Task<string> FetchReportPage(string url, ILambdaContext ctx, System.Threading.CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, token);
            ctx.Logger.LogLine($"[sync] fetch response: {(int)response.StatusCode} {response.StatusCode}");

            var html = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                ctx.Logger.LogLine($"[sync] fetch non-success — body preview: {html[..Math.Min(html.Length, 500)]}");
                return "";
            }

            ctx.Logger.LogLine($"[sync] fetched {html.Length} chars from report page");

            var text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"[ \t]{2,}", " ");
            text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n").Trim();

            return text.Length > 100_000 ? text[..100_000] : text;
        }
        catch (OperationCanceledException)
        {
            ctx.Logger.LogLine("[sync] fetch cancelled — Lambda timeout approaching");
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] fetch error: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    // ── Claude ────────────────────────────────────────────────────────────────

    private async Task<string?> CallClaude(
        string reportText, string reportUrl, string lastSynced, string apiKey, ILambdaContext ctx,
        System.Threading.CancellationToken token)
    {
        string userMessage =
            $"Events already logged cover dates up to {lastSynced}. " +
            $"The source_url for all new events in this report is: {reportUrl}\n\n" +
            $"Please process the following CTP-ISW report:\n\n" +
            reportText;

        // Tool definition for search_strikes
        object searchStrikesTool = new
        {
            name        = "search_strikes",
            description = "Search for existing strike events near a given location and date. Use this when an event may be an update to a previously recorded strike.",
            input_schema = new
            {
                type       = "object",
                properties = new
                {
                    lat         = new { type = "number", description = "Approximate latitude from the report" },
                    lng         = new { type = "number", description = "Approximate longitude from the report" },
                    date        = new { type = "string", description = "YYYY-MM-DD — the date the original event occurred" },
                    description = new { type = "string", description = "Brief description of the event to help distinguish candidates" }
                },
                required = new[] { "lat", "lng", "date" }
            }
        };

        List<object> messages = new()
        {
            new { role = "user", content = userMessage }
        };

        for (int turn = 0; turn < 10; turn++)
        {
            string requestBody = JsonSerializer.Serialize(new
            {
                model      = "claude-sonnet-4-6",
                max_tokens = 16000,
                system     = SystemPrompt,
                tools      = new[] { searchStrikesTool },
                messages
            });

            using HttpRequestMessage req = new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            HttpResponseMessage res = await Http.SendAsync(req, token);
            string resBody = await res.Content.ReadAsStringAsync(token);
            ctx.Logger.LogLine($"[sync] Claude status (turn {turn}): {res.StatusCode}");

            if (!res.IsSuccessStatusCode)
            {
                ctx.Logger.LogLine($"[sync] Claude API error: {resBody[..Math.Min(resBody.Length, 500)]}");
                return null;
            }

            JsonDocument responseDoc = JsonDocument.Parse(resBody);
            string stopReason = responseDoc.RootElement.TryGetProperty("stop_reason", out JsonElement srEl)
                ? srEl.GetString() ?? "end_turn"
                : "end_turn";
            JsonElement contentEl = responseDoc.RootElement.GetProperty("content").Clone();

            if (stopReason == "tool_use")
            {
                // Add the assistant turn (with tool_use blocks) to the conversation
                messages.Add(new { role = "assistant", content = contentEl });

                // Execute each tool call and collect results
                List<object> toolResults = new();
                foreach (JsonElement block in contentEl.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out JsonElement typeEl) || typeEl.GetString() != "tool_use")
                        continue;

                    string toolId   = block.GetProperty("id").GetString()!;
                    string toolName = block.GetProperty("name").GetString()!;
                    JsonElement toolInput = block.GetProperty("input");

                    ctx.Logger.LogLine($"[sync] tool call: {toolName} id={toolId}");

                    string resultJson;
                    if (toolName == "search_strikes")
                    {
                        object result = await ExecuteSearchStrikes(toolInput, ctx, token);
                        resultJson = JsonSerializer.Serialize(result);
                    }
                    else
                    {
                        resultJson = JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" });
                    }

                    toolResults.Add(new { type = "tool_result", tool_use_id = toolId, content = resultJson });
                }

                messages.Add(new { role = "user", content = toolResults });
                continue;
            }

            // end_turn — extract text from first text block
            string text = "";
            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement t) && t.GetString() == "text")
                {
                    text = block.GetProperty("text").GetString() ?? "";
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ctx.Logger.LogLine("[sync] Claude returned empty text");
                ctx.Logger.LogLine($"[sync] raw response body: {resBody[..Math.Min(resBody.Length, 1000)]}");
                return null;
            }

            return ExtractJson(text, ctx);
        }

        ctx.Logger.LogLine("[sync] Claude tool call loop exceeded maximum turns");
        return null;
    }

    private string? ExtractJson(string text, ILambdaContext ctx)
    {
        int start = text.IndexOf('{');
        if (start == -1)
        {
            ctx.Logger.LogLine($"[sync] no JSON object in Claude response: {text[..Math.Min(text.Length, 1000)]}");
            return null;
        }

        // Walk the text to find the balanced closing brace — LastIndexOf('}') can pick up
        // stray braces in any trailing commentary Claude adds after the JSON block.
        int depth = 0;
        bool inString = false;
        bool escaped  = false;
        int end = -1;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)               { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true;  continue; }
            if (c == '"')              { inString = !inString; continue; }
            if (inString)                continue;
            if      (c == '{')         depth++;
            else if (c == '}')         { if (--depth == 0) { end = i; break; } }
        }

        if (end == -1)
        {
            ctx.Logger.LogLine($"[sync] unbalanced JSON braces in Claude response: {text[..Math.Min(text.Length, 1000)]}");
            return null;
        }

        string jsonText = text[start..(end + 1)];

        try
        {
            JsonDocument parsed        = JsonDocument.Parse(jsonText);
            bool hasNew          = parsed.RootElement.TryGetProperty("new",          out JsonElement nv) && nv.ValueKind == JsonValueKind.Array;
            bool hasToolUpdates  = parsed.RootElement.TryGetProperty("tool_updates", out JsonElement tv) && tv.ValueKind == JsonValueKind.Array;
            bool hasAmbig        = parsed.RootElement.TryGetProperty("ambiguous",    out JsonElement av) && av.ValueKind == JsonValueKind.Array;

            if (!hasNew && !hasToolUpdates && !hasAmbig)
            {
                ctx.Logger.LogLine($"[sync] Claude response missing all expected arrays — json: {jsonText[..Math.Min(jsonText.Length, 1000)]}");
                return null;
            }

            ctx.Logger.LogLine(
                $"[sync] Claude response valid: new={( hasNew ? nv.GetArrayLength() : 0 )} " +
                $"tool_updates={( hasToolUpdates ? tv.GetArrayLength() : 0 )} " +
                $"ambiguous={( hasAmbig ? av.GetArrayLength() : 0 )}");
            return jsonText;
        }
        catch (OperationCanceledException)
        {
            ctx.Logger.LogLine("[sync] Claude call cancelled — Lambda timeout approaching");
            throw;
        }
        catch (JsonException ex)
        {
            ctx.Logger.LogLine($"[sync] Claude JSON parse error: {ex.Message}");
            ctx.Logger.LogLine($"[sync] raw Claude text: {text[..Math.Min(text.Length, 1000)]}");
            return null;
        }
    }

    private async Task<object> ExecuteSearchStrikes(JsonElement input, ILambdaContext ctx,
        System.Threading.CancellationToken token)
    {
        if (!input.TryGetProperty("lat",  out JsonElement latEl)  || latEl.ValueKind  != JsonValueKind.Number ||
            !input.TryGetProperty("lng",  out JsonElement lngEl)  || lngEl.ValueKind  != JsonValueKind.Number ||
            !input.TryGetProperty("date", out JsonElement dateEl) || dateEl.ValueKind != JsonValueKind.String)
        {
            ctx.Logger.LogLine("[sync] search_strikes: missing required inputs (lat/lng/date)");
            return new { matches = Array.Empty<object>() };
        }

        double lat  = latEl.GetDouble();
        double lng  = lngEl.GetDouble();
        string date = dateEl.GetString()!;

        DateTime parsedDate   = DateTime.Parse(date);
        string[] datesToQuery =
        [
            parsedDate.AddDays(-1).ToString("yyyy-MM-dd"),
            date,
            parsedDate.AddDays(1).ToString("yyyy-MM-dd")
        ];

        List<Dictionary<string, AttributeValue>> allCandidates = new();
        foreach (string queryDate in datesToQuery)
        {
            QueryResponse queryResp = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName                 = StrikesTable,
                IndexName                 = "entity-date-index",
                KeyConditionExpression    = "entity = :entity AND #d = :date",
                ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":entity"] = new() { S = "strike" },
                    [":date"]   = new() { S = queryDate }
                }
            }, token);
            allCandidates.AddRange(queryResp.Items);
        }

        const double RadiusKm = 25.0;
        List<object> matches = allCandidates
            .Where(i => i.ContainsKey("lat") && i.ContainsKey("lng"))
            .Select(i =>
            {
                double iLat = double.Parse(i["lat"].N);
                double iLng = double.Parse(i["lng"].N);
                double dist = HaversineKm(lat, lng, iLat, iLng);
                return (item: i, dist);
            })
            .Where(x => x.dist <= RadiusKm)
            .OrderBy(x => x.dist)
            .Take(5)
            .Select(x =>
            {
                x.item.TryGetValue("id",          out AttributeValue? idV);
                x.item.TryGetValue("date",         out AttributeValue? dateV);
                x.item.TryGetValue("title",        out AttributeValue? titleV);
                x.item.TryGetValue("location",     out AttributeValue? locationV);
                x.item.TryGetValue("lat",          out AttributeValue? latV);
                x.item.TryGetValue("lng",          out AttributeValue? lngV);
                x.item.TryGetValue("actor",        out AttributeValue? actorV);
                x.item.TryGetValue("type",         out AttributeValue? typeV);
                x.item.TryGetValue("description",  out AttributeValue? descV);
                return (object)new
                {
                    id          = idV?.S,
                    date        = dateV?.S,
                    title       = titleV?.S,
                    location    = locationV?.S,
                    lat         = latV?.N,
                    lng         = lngV?.N,
                    actor       = actorV?.S,
                    type        = typeV?.S,
                    description = descV?.S,
                    distance_km = Math.Round(x.dist, 1)
                };
            })
            .ToList();

        ctx.Logger.LogLine($"[sync] search_strikes: lat={lat} lng={lng} date={date} → {matches.Count} candidate(s)");
        return new { matches };
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // ── Economic signals ──────────────────────────────────────────────────────

    private async Task WriteEconomicSignals(JsonElement economic, string sourceUrl, ILambdaContext ctx,
        System.Threading.CancellationToken token)
    {
        if (!economic.TryGetProperty("date", out JsonElement dateEl) || dateEl.ValueKind != JsonValueKind.String)
        {
            ctx.Logger.LogLine("[sync] economic object missing 'date' — skipping write");
            return;
        }
        string date = dateEl.GetString()!;

        string hormuzStatus = economic.TryGetProperty("hormuz_status", out JsonElement hormuzEl) && hormuzEl.ValueKind == JsonValueKind.String
            ? hormuzEl.GetString()!
            : "no_alert";

        var item = new Dictionary<string, AttributeValue>
        {
            ["date"]          = new() { S = date },
            ["source_url"]    = new() { S = sourceUrl },
            ["entity"]        = new() { S = "signal" },
            ["hormuz_status"] = new() { S = hormuzStatus }
        };

        if (economic.TryGetProperty("economic_notes", out JsonElement notesEl) && notesEl.ValueKind == JsonValueKind.Array)
        {
            List<AttributeValue> noteValues = notesEl.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                .Select(n => new AttributeValue { S = n.GetString()! })
                .ToList();
            if (noteValues.Count > 0)
                item["economic_notes"] = new() { L = noteValues };
        }

        await _dynamo.PutItemAsync(new PutItemRequest { TableName = SignalsTable, Item = item }, token);
        ctx.Logger.LogLine($"[sync] economic signals written: date={date} hormuz={hormuzStatus} source_url={sourceUrl}");
    }

    // ── S3 ────────────────────────────────────────────────────────────────────

    private async Task MoveS3Object(string sourceKey, string destKey, ILambdaContext ctx)
    {
        await _s3.CopyObjectAsync(EmailBucket, sourceKey, EmailBucket, destKey);
        await _s3.DeleteObjectAsync(EmailBucket, sourceKey);
        ctx.Logger.LogLine($"[sync] moved {sourceKey} → {destKey}");
    }

    // ── DynamoDB ──────────────────────────────────────────────────────────────

    private async Task WriteSyncRecord(string runId, string status, int newEventCount, int updateCount,
        int ambigCount, string? errorMessage, string? reportUrl, string? urlStrategy = null,
        List<string>? syncNotes = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["report_url"]        = new() { S = reportUrl ?? "unknown" },
            ["run_id"]            = new() { S = runId },
            ["entity"]            = new() { S = "sync" },
            ["status"]            = new() { S = status },
            ["new_event_count"]   = new() { N = newEventCount.ToString() },
            ["update_count"]      = new() { N = updateCount.ToString() },
            ["dead_letter_count"] = new() { N = ambigCount.ToString() }
        };

        if (DateTime.TryParse(runId, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime startTime))
            item["sync_duration_ms"] = new() { N = ((long)(DateTime.UtcNow - startTime).TotalMilliseconds).ToString() };
        if (!string.IsNullOrEmpty(errorMessage))
            item["error_message"] = new() { S = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage };
        if (!string.IsNullOrEmpty(urlStrategy))
            item["url_strategy"] = new() { S = urlStrategy };
        if (syncNotes is { Count: > 0 })
            item["sync_notes"] = new() { L = syncNotes.Select(n => new AttributeValue { S = n }).ToList() };

        string? reportDate = ParseReportDateFromUrl(reportUrl);
        if (!string.IsNullOrEmpty(reportDate))
            item["report_date"] = new() { S = reportDate };

        await _dynamo.PutItemAsync(new PutItemRequest { TableName = SyncsTable, Item = item });
    }

    // Extracts YYYY-MM-DD from a CTP-ISW report URL.
    // URL slugs always end with {month}-{day}-{year}, e.g. iran-update-april-13-2026.
    internal static string? ParseReportDateFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        System.Text.RegularExpressions.Match m =
            System.Text.RegularExpressions.Regex.Match(url, @"([a-z]+)-(\d{1,2})-(\d{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var months = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["january"] = "01", ["february"] = "02", ["march"]     = "03",
            ["april"]   = "04", ["may"]       = "05", ["june"]      = "06",
            ["july"]    = "07", ["august"]    = "08", ["september"] = "09",
            ["october"] = "10", ["november"]  = "11", ["december"]  = "12"
        };

        if (!months.TryGetValue(m.Groups[1].Value, out string? month)) return null;

        string day  = m.Groups[2].Value.PadLeft(2, '0');
        string year = m.Groups[3].Value;
        return $"{year}-{month}-{day}";
    }

    // ── SSM ───────────────────────────────────────────────────────────────────

    private async Task<(string anthropicKey, string lastSynced)> ReadSsmParams()
    {
        var response = await _ssm.GetParametersAsync(new GetParametersRequest
        {
            Names          = new List<string> { $"{SsmPrefix}/anthropic_api_key", $"{SsmPrefix}/last_synced" },
            WithDecryption = true
        });

        string Get(string suffix) =>
            response.Parameters.FirstOrDefault(p => p.Name.EndsWith(suffix))?.Value ?? "";

        return (
            anthropicKey: Get("anthropic_api_key"),
            lastSynced:   Get("last_synced") is { Length: > 0 } s ? s : "2026-02-27"
        );
    }

    private async Task UpdateSsmParam(string name, string value)
    {
        await _ssm.PutParameterAsync(new PutParameterRequest
        {
            Name      = name,
            Value     = value,
            Type      = ParameterType.String,
            Overwrite = true
        });
    }

    // Strip query string, fragment, trailing slash; lowercase host and path
    private static string NormalizeUrl(string url)
    {
        var uri = new Uri(url.Trim());
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()}";
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

// ── Message model ─────────────────────────────────────────────────────────────

public record ReportMessage(
    [property: JsonPropertyName("run_id")]       string? RunId,
    [property: JsonPropertyName("url")]          string  Url,
    [property: JsonPropertyName("email_key")]    string? EmailKey,
    [property: JsonPropertyName("url_strategy")] string? UrlStrategy
);
