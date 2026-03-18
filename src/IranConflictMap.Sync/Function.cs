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
        Timeout = TimeSpan.FromMinutes(4)
    };

    private readonly IAmazonDynamoDB                  _dynamo;
    private readonly IAmazonSimpleSystemsManagement   _ssm;
    private readonly IAmazonSQS                       _sqs;
    private readonly IAmazonS3                        _s3;

    private static readonly string StrikesTable      = Env("STRIKES_TABLE",       "strikes");
    private static readonly string SyncsTable        = Env("SYNCS_TABLE",         "syncs");
    private static readonly string SsmPrefix         = Env("SSM_PREFIX",          "/iran-conflict-map");
    private static readonly string ProcessorQueueUrl = Env("PROCESSOR_QUEUE_URL", "");
    private static readonly string EmailBucket       = Env("EMAIL_BUCKET",        "");

    // ── System prompt ─────────────────────────────────────────────────────────
    private const string SystemPrompt = """
        I'm building an Iran/Middle East conflict map. I need you to process a CTP-ISW Iran update report and extract strike events into structured DynamoDB data.
        Schema:

        id (String) — unique numeric string, increment from the last ID I give you (new events only)
        entity (String) — always "strike" (GSI partition key, required)
        date (String) — ISO 8601, YYYY-MM-DD
        title (String) — short event title
        location (String) — human-readable place name
        lat / lng (Number) — decimal degrees
        type (String) — one of: strike, drone, naval, missile
        target_type (String) — one of: military, maritime, nuclear, command, civilian
        actor (String) — free text, the attacking party (e.g. US, Israel, Iran, Saudi Arabia, Houthi, etc.)
        severity (String) — one of: low, medium, high, critical
        description (String) — 1–3 sentence factual summary
        casualties (Map) — { confirmed: N, estimated: N }
        source_url (String, optional) — the CTP-ISW report URL this event was extracted from; same value for every event extracted from a given report
        citations (List of Strings, optional) — URLs of primary sources cited inline in this event's topline paragraph; resolve each footnote marker (e.g. [i], [ii], [xv]) to its full URL from the footnote block at the bottom of the report; only include footnotes directly associated with this event's paragraph; omit if none
        disputed (Boolean, optional) — set to true if the event is contested, unverified, or denied by a party; omit if not disputed

        Severity guidelines:
        low — minor incident, 0 casualties, warning shots, disputed/intercepted attacks
        medium — limited engagement, 1–10 casualties, localized damage
        high — significant strike, 10–50 casualties, major infrastructure or military target
        critical — mass casualty event (50+ estimated), nuclear facility strike, decapitation strike, or major strategic escalation

        Granularity rule: Generate one event per distinct operation or topline paragraph. Do not log individual munitions within a barrage as separate events. A single coordinated wave of strikes on the same target type in the same location on the same date = one event.
        Source URL rule: Set source_url to the full URL of the CTP-ISW report being processed. This is the same value for every event extracted from a given report.
        Citation mapping rule: The report contains inline footnote markers (e.g. [i], [ii], [xv]) within each topline paragraph, and a corresponding footnote block at the bottom resolving each marker to a URL. For each event, collect only the footnote markers within that event's paragraph, resolve them to their URLs, and include those in citations. Omit the field entirely if no footnotes are mappable.
        New vs update rule: Classify each extracted event as:
        new — not previously reported; assign the next available ID
        update — reported in a prior report, this report adds detail or corrects it; include only changed fields plus date, lat, and lng as lookup keys (lat/lng as plain decimal numbers, not DynamoDB wire format); omit location and actor from the lookup
        ambiguous — cannot confidently determine whether new or update; include a "note" explaining the uncertainty, a complete "as_new" item (same PutRequest/Item wire format as new events), and an "as_update" payload (same lookup/changes format as updates); only use ambiguous when genuinely uncertain — prefer new or update if you can reasonably classify the event

        Output format — return a single JSON object:
        {
          "new": [ { "PutRequest": { "Item": { ... DynamoDB wire format ... } } }, ... ],
          "updates": [ { "lookup": { "date": "...", "lat": 0.0, "lng": 0.0 }, "changes": { ... DynamoDB wire format ... } }, ... ],
          "ambiguous": [ { "note": "...", "as_new": { "PutRequest": { "Item": { ... DynamoDB wire format ... } } }, "as_update": { "lookup": { "date": "...", "lat": 0.0, "lng": 0.0 }, "changes": { ... DynamoDB wire format ... } } }, ... ]
        }
        Only include "disputed" when genuinely contested. Only include "citations" when footnotes are mappable. Only include fields in "changes" that are actually changing.
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

        var reportUrl   = msg.Url;
        var emailKey    = msg.EmailKey;     // null for manual submissions
        var urlStrategy = msg.UrlStrategy;  // null for manual submissions
        var runId       = DateTime.UtcNow.ToString("o");

        context.Logger.LogLine($"[sync] run started: {runId}");
        context.Logger.LogLine($"[sync] url={reportUrl}  email_key={emailKey ?? "(none)"}  url_strategy={urlStrategy ?? "(manual)"}");

        try
        {
            var (anthropicKey, lastSynced) = await ReadSsmParams();
            context.Logger.LogLine($"[sync] last_synced={lastSynced}");

            // ── 1. Fetch report page ──────────────────────────────────────────
            var reportText = await FetchReportPage(reportUrl, context);
            if (string.IsNullOrWhiteSpace(reportText))
            {
                context.Logger.LogLine("[sync] report page returned empty content");
                await WriteSyncRecord(runId, "fetch_error", 0, 0, 0,
                    $"Empty or failed response fetching {reportUrl}", reportUrl, urlStrategy);
                return;
            }

            // ── 2. Call Claude ────────────────────────────────────────────────
            var nextId = await GetNextId();
            context.Logger.LogLine($"[sync] calling Claude, nextId={nextId}");

            var claudeJson = await CallClaude(reportText, reportUrl, lastSynced, nextId, anthropicKey, context);
            if (claudeJson == null)
            {
                context.Logger.LogLine("[sync] Claude returned no usable response");
                await WriteSyncRecord(runId, "claude_error", 0, 0, 0,
                    "Claude returned empty or malformed JSON — possible JS-rendered content or wrong page", reportUrl, urlStrategy);
                return;
            }

            // ── 3. Push to processor SQS ──────────────────────────────────────
            // runId is used as synced_at so the Processor can UpdateItem the same record
            var claudeDoc = JsonDocument.Parse(claudeJson).RootElement;
            var envelope  = JsonSerializer.Serialize(new
            {
                source_url = reportUrl,
                synced_at  = runId,
                @new       = claudeDoc.TryGetProperty("new",       out var n) ? n : (JsonElement?)null,
                updates    = claudeDoc.TryGetProperty("updates",   out var u) ? u : (JsonElement?)null,
                ambiguous  = claudeDoc.TryGetProperty("ambiguous", out var a) ? a : (JsonElement?)null
            });

            // Write initial record before pushing — Processor will update counts/status
            await WriteSyncRecord(runId, "processing", 0, 0, 0, null, reportUrl, urlStrategy);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl       = ProcessorQueueUrl,
                MessageBody    = envelope,
                MessageGroupId = "sync"
            });
            context.Logger.LogLine("[sync] Claude response pushed to processor queue");

            // ── 4. Move email to processed (only if we have an email key) ─────
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

    private async Task<string> FetchReportPage(string url, ILambdaContext ctx)
    {
        try
        {
            var html = await Http.GetStringAsync(url);
            ctx.Logger.LogLine($"[sync] fetched {html.Length} chars from report page");

            var text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"[ \t]{2,}", " ");
            text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n").Trim();

            return text.Length > 100_000 ? text[..100_000] : text;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] fetch error: {ex.Message}");
            return "";
        }
    }

    // ── Claude ────────────────────────────────────────────────────────────────

    private async Task<string?> CallClaude(
        string reportText, string reportUrl, string lastSynced, int nextId, string apiKey, ILambdaContext ctx)
    {
        var lastId = nextId - 1;
        var userMessage =
            $"The last used ID is {lastId}. " +
            $"Events already logged cover dates up to {lastSynced}. " +
            $"The source_url for all new events in this report is: {reportUrl}\n\n" +
            $"Please process the following CTP-ISW report and generate output starting from ID {nextId}:\n\n" +
            reportText;

        var requestBody = JsonSerializer.Serialize(new
        {
            model      = "claude-sonnet-4-6",
            max_tokens = 16000,
            system     = SystemPrompt,
            messages   = new[] { new { role = "user", content = userMessage } }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var res     = await Http.SendAsync(req);
        var resBody = await res.Content.ReadAsStringAsync();
        ctx.Logger.LogLine($"[sync] Claude status: {res.StatusCode}");

        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogLine($"[sync] Claude API error: {resBody[..Math.Min(resBody.Length, 500)]}");
            return null;
        }

        var doc  = JsonDocument.Parse(resBody);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            ctx.Logger.LogLine("[sync] Claude returned empty text");
            return null;
        }

        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start == -1 || end == -1 || end <= start)
        {
            ctx.Logger.LogLine($"[sync] no JSON object in Claude response: {text[..Math.Min(text.Length, 300)]}");
            return null;
        }

        var jsonText = text[start..(end + 1)];

        try
        {
            var parsed     = JsonDocument.Parse(jsonText);
            var hasNew     = parsed.RootElement.TryGetProperty("new",       out var nv) && nv.ValueKind == JsonValueKind.Array;
            var hasUpdates = parsed.RootElement.TryGetProperty("updates",   out var uv) && uv.ValueKind == JsonValueKind.Array;
            var hasAmbig   = parsed.RootElement.TryGetProperty("ambiguous", out var av) && av.ValueKind == JsonValueKind.Array;

            if (!hasNew && !hasUpdates && !hasAmbig)
            {
                ctx.Logger.LogLine("[sync] Claude response missing all three expected arrays");
                return null;
            }

            ctx.Logger.LogLine($"[sync] Claude response valid: new={nv.GetArrayLength()} updates={uv.GetArrayLength()} ambiguous={av.GetArrayLength()}");
            return jsonText;
        }
        catch (JsonException ex)
        {
            ctx.Logger.LogLine($"[sync] Claude JSON parse error: {ex.Message}");
            return null;
        }
    }

    // ── S3 ────────────────────────────────────────────────────────────────────

    private async Task MoveS3Object(string sourceKey, string destKey, ILambdaContext ctx)
    {
        await _s3.CopyObjectAsync(EmailBucket, sourceKey, EmailBucket, destKey);
        await _s3.DeleteObjectAsync(EmailBucket, sourceKey);
        ctx.Logger.LogLine($"[sync] moved {sourceKey} → {destKey}");
    }

    // ── DynamoDB ──────────────────────────────────────────────────────────────

    private async Task<int> GetNextId()
    {
        var response = await _dynamo.ScanAsync(new ScanRequest
        {
            TableName            = StrikesTable,
            ProjectionExpression = "id"
        });
        return response.Items
            .Select(i => int.TryParse(i["id"].S, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private async Task WriteSyncRecord(string id, string status, int newEventCount, int updateCount,
        int ambigCount, string? errorMessage, string? reportUrl, string? urlStrategy = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["id"]                = new() { S = id },
            ["entity"]            = new() { S = "sync" },
            ["timestamp"]         = new() { S = id },
            ["status"]            = new() { S = status },
            ["new_event_count"]   = new() { N = newEventCount.ToString() },
            ["update_count"]      = new() { N = updateCount.ToString() },
            ["dead_letter_count"] = new() { N = ambigCount.ToString() }
        };
        if (!string.IsNullOrEmpty(errorMessage))
            item["error_message"] = new() { S = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage };
        if (!string.IsNullOrEmpty(reportUrl))
            item["report_url"] = new() { S = reportUrl };
        if (!string.IsNullOrEmpty(urlStrategy))
            item["url_strategy"] = new() { S = urlStrategy };

        await _dynamo.PutItemAsync(new PutItemRequest { TableName = SyncsTable, Item = item });
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

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

// ── Message model ─────────────────────────────────────────────────────────────

public record ReportMessage(
    [property: JsonPropertyName("url")]          string  Url,
    [property: JsonPropertyName("email_key")]    string? EmailKey,
    [property: JsonPropertyName("url_strategy")] string? UrlStrategy
);
