using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using MimeKit;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Sync;

public class Function
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect        = true,
        MaxAutomaticRedirections = 10
    });

    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly IAmazonSQS _sqs;
    private readonly IAmazonS3 _s3;

    private static readonly string StrikesTable      = Env("STRIKES_TABLE",       "strikes");
    private static readonly string SyncsTable        = Env("SYNCS_TABLE",         "syncs");
    private static readonly string SsmPrefix         = Env("SSM_PREFIX",          "/iran-conflict-map");
    private static readonly string ProcessorQueueUrl = Env("PROCESSOR_QUEUE_URL", "");
    private static readonly string EmailBucket       = Env("EMAIL_BUCKET",        "");
    private static readonly string InboxPrefix       = Env("EMAIL_INBOX_PREFIX",  "inbox/");
    private static readonly string OtherPrefix       = Env("EMAIL_OTHER_PREFIX",  "other/");

    // Filter: sender + (subject OR body) must contain "Iran" and "Update"
    private const string ExpectedSender   = "criticalthreats@aei.org";
    private const string ExpectedKeyword1 = "Iran";
    private const string ExpectedKeyword2 = "Update";

    // ── Extraction system prompt ──────────────────────────────────────────────
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
        update — reported in a prior report, this report adds detail or corrects it; include only changed fields plus date, location, actor as lookup keys
        ambiguous — cannot confidently determine whether new or update; include a note explaining why

        Output format — return a single JSON object:
        {
          "new": [ { "PutRequest": { "Item": { ... DynamoDB wire format ... } } }, ... ],
          "updates": [ { "lookup": { "date": "...", "location": "...", "actor": "..." }, "changes": { ... } }, ... ],
          "ambiguous": [ { "note": "...", "raw": {} }, ... ]
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

    // For testing
    public Function(IAmazonDynamoDB dynamo, IAmazonSimpleSystemsManagement ssm, IAmazonSQS sqs, IAmazonS3 s3)
    {
        _dynamo = dynamo;
        _ssm    = ssm;
        _sqs    = sqs;
        _s3     = s3;
    }

    public async Task<string> FunctionHandler(object input, ILambdaContext context)
    {
        var runId = DateTime.UtcNow.ToString("o");
        context.Logger.LogLine($"[sync] run started: {runId}");

        try
        {
            var (anthropicKey, lastSynced) = await ReadSsmParams();
            context.Logger.LogLine($"[sync] last_synced={lastSynced}");

            // ── 1. Find all CTP-ISW emails in inbox ───────────────────────────
            var emails = await FindAllCtpEmails(context);
            if (emails.Count == 0)
            {
                context.Logger.LogLine("[sync] no CTP-ISW email found in inbox");
                await WriteSyncRecord(runId, "no_email", 0, 0, 0, null, null);
                return "no_email";
            }
            context.Logger.LogLine($"[sync] found {emails.Count} CTP-ISW email(s) to process");

            var totalNew    = 0;
            var totalUpdate = 0;
            var totalAmbig  = 0;
            var processed   = 0;

            foreach (var (emailKey, emailMessage) in emails)
            {
                var emailRunId = emails.Count == 1 ? runId : $"{runId}#{processed}";
                context.Logger.LogLine($"[sync] processing [{processed + 1}/{emails.Count}]: subject='{emailMessage.Subject}'");

                // ── 2. Extract and resolve report URL ─────────────────────────
                var htmlBody   = emailMessage.HtmlBody ?? "";
                var hubspotUrl = ExtractHubspotLink(htmlBody, context);
                if (hubspotUrl == null)
                {
                    context.Logger.LogLine("[sync] no Iran Update link found in email");
                    await WriteSyncRecord(emailRunId, "no_url", 0, 0, 0,
                        "Could not find Iran Update link in email body", null);
                    await MoveS3Object(emailKey, "processed/" + emailKey[InboxPrefix.Length..], context);
                    processed++;
                    continue;
                }

                var reportUrl = await FollowRedirect(hubspotUrl, context);
                if (reportUrl == null || !reportUrl.Contains("criticalthreats.org/analysis/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Logger.LogLine($"[sync] redirect did not lead to criticalthreats.org/analysis: {reportUrl}");
                    await WriteSyncRecord(emailRunId, "no_url", 0, 0, 0,
                        $"Link did not redirect to a criticalthreats.org report (got: {reportUrl})", null);
                    await MoveS3Object(emailKey, "processed/" + emailKey[InboxPrefix.Length..], context);
                    processed++;
                    continue;
                }
                context.Logger.LogLine($"[sync] report URL: {reportUrl}");

                // ── 3. Fetch report page ───────────────────────────────────────
                var reportText = await FetchReportPage(reportUrl, context);
                if (string.IsNullOrWhiteSpace(reportText))
                {
                    context.Logger.LogLine("[sync] report page returned empty content");
                    await WriteSyncRecord(emailRunId, "fetch_error", 0, 0, 0,
                        $"Empty or failed response fetching {reportUrl}", reportUrl);
                    processed++;
                    continue;
                }

                // ── 4. Call Claude Haiku ───────────────────────────────────────
                var nextId = await GetNextId();
                context.Logger.LogLine($"[sync] calling Claude, nextId={nextId}");

                var claudeJson = await CallClaude(reportText, reportUrl, lastSynced, nextId, anthropicKey, context);
                if (claudeJson == null)
                {
                    context.Logger.LogLine("[sync] Claude returned no usable response");
                    await WriteSyncRecord(emailRunId, "claude_error", 0, 0, 0,
                        "Claude returned empty or malformed JSON — possible wrong page fetched or JS-rendered content", reportUrl);
                    processed++;
                    continue;
                }

                // ── 5. Push to processor SQS ──────────────────────────────────
                await _sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl    = ProcessorQueueUrl,
                    MessageBody = claudeJson
                });
                context.Logger.LogLine("[sync] Claude response pushed to SQS");

                // ── 6. Move email to processed ────────────────────────────────
                await MoveS3Object(emailKey, "processed/" + emailKey[InboxPrefix.Length..], context);

                // ── 7. Write sync record ──────────────────────────────────────
                var doc         = JsonDocument.Parse(claudeJson);
                var newCount    = doc.RootElement.TryGetProperty("new",       out var nArr) ? nArr.GetArrayLength() : 0;
                var updateCount = doc.RootElement.TryGetProperty("updates",   out var uArr) ? uArr.GetArrayLength() : 0;
                var ambigCount  = doc.RootElement.TryGetProperty("ambiguous", out var aArr) ? aArr.GetArrayLength() : 0;

                totalNew    += newCount;
                totalUpdate += updateCount;
                totalAmbig  += ambigCount;

                // Flag if Claude returned valid JSON but extracted nothing at all — possible JS-render or wrong page
                var syncStatus = (newCount + updateCount + ambigCount) == 0 ? "no_events" : "success";
                if (syncStatus == "no_events")
                    context.Logger.LogLine("[sync] warning: Claude returned valid JSON but no events — possible JS-rendered page or empty report");

                await WriteSyncRecord(emailRunId, syncStatus, newCount, updateCount, ambigCount,
                    syncStatus == "no_events" ? "Claude returned no events — report page may be JS-rendered or have no new events" : null,
                    reportUrl);
                await UpdateSsmParam($"{SsmPrefix}/last_synced", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                context.Logger.LogLine($"[sync] email {processed + 1} done — status={syncStatus} new={newCount} updates={updateCount} ambiguous={ambigCount}");
                processed++;
            }

            context.Logger.LogLine($"[sync] all done — processed={processed} new={totalNew} updates={totalUpdate} ambiguous={totalAmbig}");
            return $"success:{totalNew}";
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"[sync] unhandled error: {ex}");
            await WriteSyncRecord(runId, "error", 0, 0, 0,
                ex.Message[..Math.Min(ex.Message.Length, 1000)], null);
            throw;
        }
    }

    // ── S3 Email Reading ─────────────────────────────────────────────────────

    private async Task<List<(string key, MimeMessage message)>> FindAllCtpEmails(ILambdaContext ctx)
    {
        var listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = EmailBucket,
            Prefix     = InboxPrefix
        });

        var objects = listResp.S3Objects
            .Where(o => o.Key != InboxPrefix)           // skip folder placeholder
            .OrderByDescending(o => o.LastModified)
            .ToList();

        ctx.Logger.LogLine($"[sync] {objects.Count} objects in inbox");

        var matches = new List<(string key, MimeMessage message)>();

        foreach (var obj in objects)
        {
            var message = await ReadMimeMessage(obj.Key, ctx);
            if (message == null) continue;

            var (isMatch, reason) = IsCtpIswEmail(message);
            if (isMatch)
            {
                ctx.Logger.LogLine($"[sync] matched ({reason}): {obj.Key}");
                matches.Add((obj.Key, message));
            }
            else
            {
                var from    = message.From.Mailboxes.FirstOrDefault()?.Address ?? "(unknown)";
                var subject = message.Subject ?? "";
                ctx.Logger.LogLine($"[sync] skipping: from={from} subject={subject[..Math.Min(subject.Length, 60)]}");
                await MoveS3Object(obj.Key, OtherPrefix + obj.Key[InboxPrefix.Length..], ctx);
            }
        }

        // Return oldest-first so updates are applied in chronological order
        matches.Reverse();
        return matches;
    }

    /// <summary>
    /// Returns (true, reason) if the message is a CTP-ISW Iran daily update.
    /// Primary: sender + subject contains "Iran" and "Update".
    /// Fallback: sender + body text contains "Iran" and "Update" (in case subject format changes).
    /// </summary>
    private static (bool match, string reason) IsCtpIswEmail(MimeMessage message)
    {
        var from    = message.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var subject = message.Subject ?? "";

        if (!from.Equals(ExpectedSender, StringComparison.OrdinalIgnoreCase))
            return (false, "");

        // Primary: subject contains "Iran" AND "Update"
        if (subject.Contains(ExpectedKeyword1, StringComparison.OrdinalIgnoreCase) &&
            subject.Contains(ExpectedKeyword2, StringComparison.OrdinalIgnoreCase))
            return (true, "sender+subject");

        // Fallback: body text contains "Iran" AND "Update"
        var body = message.TextBody ?? Regex.Replace(message.HtmlBody ?? "", @"<[^>]+>", " ");
        if (body.Contains(ExpectedKeyword1, StringComparison.OrdinalIgnoreCase) &&
            body.Contains(ExpectedKeyword2, StringComparison.OrdinalIgnoreCase))
            return (true, "sender+body");

        return (false, "");
    }

    private async Task<MimeMessage?> ReadMimeMessage(string key, ILambdaContext ctx)
    {
        try
        {
            var resp = await _s3.GetObjectAsync(EmailBucket, key);
            return await MimeMessage.LoadAsync(resp.ResponseStream);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] warning: failed to read {key}: {ex.Message}");
            return null;
        }
    }

    private async Task MoveS3Object(string sourceKey, string destKey, ILambdaContext ctx)
    {
        await _s3.CopyObjectAsync(EmailBucket, sourceKey, EmailBucket, destKey);
        await _s3.DeleteObjectAsync(EmailBucket, sourceKey);
        ctx.Logger.LogLine($"[sync] moved {sourceKey} → {destKey}");
    }

    // ── URL Extraction ───────────────────────────────────────────────────────

    private string? ExtractHubspotLink(string emailBody, ILambdaContext ctx)
    {
        var anchorRe = new Regex(@"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Primary: anchor whose visible text contains "Iran War Update"
        foreach (Match m in anchorRe.Matches(emailBody))
        {
            var href = m.Groups[1].Value;
            var text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
            if (text.Contains("Iran Update", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.LogLine($"[sync] primary match: '{text[..Math.Min(text.Length, 80)]}'");
                return href;
            }
        }

        ctx.Logger.LogLine("[sync] primary match failed — trying fallback keywords");

        // Fallback: HubSpot href whose visible text suggests a report link
        foreach (Match m in anchorRe.Matches(emailBody))
        {
            var href = m.Groups[1].Value;
            var text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
            if (href.Contains("hubspotlinks", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("criticalthreats", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("read the", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("view update", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("full update", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("full report", StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Logger.LogLine($"[sync] fallback match: '{text[..Math.Min(text.Length, 80)]}'");
                return href;
            }
        }

        ctx.Logger.LogLine("[sync] no suitable link found in email body");
        return null;
    }

    private async Task<string?> FollowRedirect(string url, ILambdaContext ctx)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var finalUrl = res.RequestMessage?.RequestUri?.ToString();
            ctx.Logger.LogLine($"[sync] redirect resolved → {finalUrl}");
            return finalUrl;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] redirect error: {ex.Message}");
            return null;
        }
    }

    // ── Report Fetch ─────────────────────────────────────────────────────────

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

    // ── Claude ───────────────────────────────────────────────────────────────

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
            model      = "claude-haiku-4-5-20251001",
            max_tokens = 8192,
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
            var hasNew     = parsed.RootElement.TryGetProperty("new",       out var n) && n.ValueKind == JsonValueKind.Array;
            var hasUpdates = parsed.RootElement.TryGetProperty("updates",   out var u) && u.ValueKind == JsonValueKind.Array;
            var hasAmbig   = parsed.RootElement.TryGetProperty("ambiguous", out var a) && a.ValueKind == JsonValueKind.Array;

            if (!hasNew && !hasUpdates && !hasAmbig)
            {
                ctx.Logger.LogLine("[sync] Claude response missing all three expected arrays");
                return null;
            }

            ctx.Logger.LogLine($"[sync] Claude response valid: new={n.GetArrayLength()} updates={u.GetArrayLength()} ambiguous={a.GetArrayLength()}");
            return jsonText;
        }
        catch (JsonException ex)
        {
            ctx.Logger.LogLine($"[sync] Claude JSON parse error: {ex.Message}");
            return null;
        }
    }

    // ── DynamoDB ─────────────────────────────────────────────────────────────

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
        int ambigCount, string? errorMessage, string? reportUrl)
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

        await _dynamo.PutItemAsync(new PutItemRequest { TableName = SyncsTable, Item = item });
    }

    // ── SSM ──────────────────────────────────────────────────────────────────

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
