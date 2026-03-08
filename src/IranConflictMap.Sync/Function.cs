using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Sync;

public class Function
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "IranConflictMap/1.0 (https://conflictmap.chrisdargis.com)");
        return client;
    }

    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonSimpleSystemsManagement _ssm;

    private static readonly string StrikesTable  = Env("STRIKES_TABLE",   "strikes");
    private static readonly string SyncsTable    = Env("SYNCS_TABLE",     "syncs");
    private static readonly string SsmPrefix     = Env("SSM_PREFIX",      "/iran-conflict-map");
    private static readonly string WikiPageTitle = Env("WIKIPEDIA_PAGE",  "2026_Israel%E2%80%93Iran_war");

    public Function()
    {
        _dynamo = new AmazonDynamoDBClient();
        _ssm    = new AmazonSimpleSystemsManagementClient();
    }

    // Entry point for EventBridge scheduled trigger and manual POST trigger
    public async Task<string> FunctionHandler(object input, ILambdaContext context)
    {
        var runId = DateTime.UtcNow.ToString("o");
        context.Logger.LogLine($"[sync] run started: {runId}");

        try
        {
            var (lastSynced, lastRevisionId, anthropicKey) = await ReadSsmParams();
            context.Logger.LogLine($"[sync] last_synced={lastSynced} last_revision={lastRevisionId}");

            var currentRevisionId = await GetCurrentRevisionId(context);
            context.Logger.LogLine($"[sync] current_revision={currentRevisionId}");

            if (currentRevisionId == lastRevisionId)
            {
                context.Logger.LogLine("[sync] no wikipedia changes — skipping");
                await WriteSyncRecord(runId, 0, false, "no_changes");
                return "no_changes";
            }

            // Detect edits to content older than lastSynced
            var hasEdits = false;
            if (!string.IsNullOrEmpty(lastRevisionId) && lastRevisionId != "none")
                hasEdits = await DetectPastEdits(lastRevisionId, currentRevisionId, lastSynced, context);

            var pageContent = await GetPageContent(context);
            var nextId      = await GetNextId();

            context.Logger.LogLine($"[sync] calling claude, nextId={nextId}");
            var newItems = await CallClaude(pageContent, lastSynced, nextId, anthropicKey, context);
            context.Logger.LogLine($"[sync] claude returned {newItems.Count} items");

            if (newItems.Count > 0)
                await WriteEvents(newItems);

            await WriteSyncRecord(runId, newItems.Count, hasEdits, "success");
            await UpdateSsmParams(DateTime.UtcNow.ToString("yyyy-MM-dd"), currentRevisionId);

            context.Logger.LogLine($"[sync] done — {newItems.Count} events, hasEdits={hasEdits}");
            return $"success:{newItems.Count}";
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"[sync] error: {ex}");
            await WriteSyncRecord(runId, 0, false, "error");
            throw;
        }
    }

    // ── SSM ────────────────────────────────────────────────────────────────────

    private async Task<(string lastSynced, string lastRevisionId, string anthropicKey)> ReadSsmParams()
    {
        var response = await _ssm.GetParametersAsync(new GetParametersRequest
        {
            Names           = [$"{SsmPrefix}/last_synced", $"{SsmPrefix}/last_revision_id", $"{SsmPrefix}/anthropic_api_key"],
            WithDecryption  = true
        });

        string Get(string suffix) =>
            response.Parameters.FirstOrDefault(p => p.Name.EndsWith(suffix))?.Value ?? "";

        return (
            lastSynced:      Get("last_synced")      is { Length: > 0 } s ? s : "2026-02-27",
            lastRevisionId:  Get("last_revision_id") is { Length: > 0 } r ? r : "none",
            anthropicKey:    Get("anthropic_api_key")
        );
    }

    private async Task UpdateSsmParams(string lastSynced, string lastRevisionId)
    {
        foreach (var (name, value) in new[] {
            ($"{SsmPrefix}/last_synced",      lastSynced),
            ($"{SsmPrefix}/last_revision_id", lastRevisionId)
        })
        {
            await _ssm.PutParameterAsync(new PutParameterRequest
            {
                Name      = name,
                Value     = value,
                Type      = ParameterType.String,
                Overwrite = true
            });
        }
    }

    // ── Wikipedia ──────────────────────────────────────────────────────────────

    private async Task<string> GetCurrentRevisionId(ILambdaContext ctx)
    {
        var url = $"https://en.wikipedia.org/w/api.php?action=query&titles={WikiPageTitle}&prop=revisions&rvprop=ids&format=json";
        var json = await Http.GetStringAsync(url);
        var doc  = JsonDocument.Parse(json);
        foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
        {
            if (page.Value.TryGetProperty("revisions", out var revs))
                return revs[0].GetProperty("revid").GetRawText();
        }
        ctx.Logger.LogLine("[sync] warning: could not parse revision id");
        return "unknown";
    }

    private async Task<bool> DetectPastEdits(string fromRev, string toRev, string lastSynced, ILambdaContext ctx)
    {
        try
        {
            var url  = $"https://en.wikipedia.org/w/api.php?action=compare&fromrev={fromRev}&torev={toRev}&prop=diff&format=json";
            var json = await Http.GetStringAsync(url);
            var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("compare", out var compare)) return false;
            var diff = compare.GetProperty("*").GetString() ?? "";

            // Look for <del> tags containing dates older than lastSynced
            var cutoff    = DateTime.Parse(lastSynced);
            var delBlocks = Regex.Matches(diff, @"<del[^>]*>(.*?)</del>", RegexOptions.Singleline);
            foreach (Match block in delBlocks)
            {
                foreach (Match dateMatch in Regex.Matches(block.Groups[1].Value, @"20\d\d-\d{2}-\d{2}"))
                {
                    if (DateTime.TryParse(dateMatch.Value, out var d) && d < cutoff)
                    {
                        ctx.Logger.LogLine($"[sync] edit detected in content from {d:yyyy-MM-dd}");
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] edit detection error (non-fatal): {ex.Message}");
            return false;
        }
    }

    private async Task<string> GetPageContent(ILambdaContext ctx)
    {
        var url     = $"https://en.wikipedia.org/api/rest_v1/page/plain/{WikiPageTitle}";
        var content = await Http.GetStringAsync(url);
        ctx.Logger.LogLine($"[sync] page content length: {content.Length}");
        // Truncate to avoid hitting context limits (~80k chars is safe for Haiku)
        return content.Length > 80_000 ? content[..80_000] : content;
    }

    // ── DynamoDB ───────────────────────────────────────────────────────────────

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

    private async Task WriteEvents(List<Dictionary<string, AttributeValue>> items)
    {
        for (int i = 0; i < items.Count; i += 25)
        {
            var batch = items.Skip(i).Take(25)
                .Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } })
                .ToList();

            await _dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { [StrikesTable] = batch }
            });
        }
    }

    private async Task WriteSyncRecord(string id, int newEventCount, bool hasEdits, string status)
    {
        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = SyncsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["id"]              = new() { S    = id },
                ["entity"]          = new() { S    = "sync" },
                ["timestamp"]       = new() { S    = id },
                ["new_event_count"] = new() { N    = newEventCount.ToString() },
                ["has_edits"]       = new() { BOOL = hasEdits },
                ["status"]          = new() { S    = status }
            }
        });
    }

    // ── Claude ─────────────────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, AttributeValue>>> CallClaude(
        string pageContent, string lastSynced, int nextId, string apiKey, ILambdaContext ctx)
    {
        var prompt = """
            You maintain a conflict map database of military events in the Iran/Middle East conflict.

            Here is a Wikipedia article about the conflict:

            WIKI_CONTENT

            Extract all military events that occurred STRICTLY AFTER LAST_SYNCED.
            Start IDs from NEXT_ID and increment by 1 for each event.

            Rules:
            - entity is always "strike"
            - actor must be one of: US, Israel, Iran
            - type must be one of: strike, drone, naval, missile
            - target_type must be one of: military, maritime, nuclear, command, civilian
            - severity: low (minor/0 cas), medium (1-10 cas), high (10-50 cas), critical (50+ cas or nuclear/decapitation)
            - Only include "disputed": {"BOOL": true} if the event is genuinely contested or denied
            - If no new events exist after LAST_SYNCED, output an empty array: []

            Output ONLY a valid JSON array of DynamoDB Item objects. No explanation, no markdown.

            Example item:
            {
              "id":          {"S": "NEXT_ID"},
              "entity":      {"S": "strike"},
              "date":        {"S": "YYYY-MM-DD"},
              "title":       {"S": "Short event title"},
              "location":    {"S": "City, Country"},
              "lat":         {"N": "00.0000"},
              "lng":         {"N": "00.0000"},
              "type":        {"S": "strike"},
              "target_type": {"S": "military"},
              "actor":       {"S": "US"},
              "severity":    {"S": "high"},
              "description": {"S": "1-3 sentence factual summary."},
              "casualties":  {"M": {"confirmed": {"N": "0"}, "estimated": {"N": "0"}}}
            }
            """
            .Replace("WIKI_CONTENT", pageContent)
            .Replace("LAST_SYNCED",  lastSynced)
            .Replace("NEXT_ID",      nextId.ToString());

        var body = JsonSerializer.Serialize(new
        {
            model      = "claude-haiku-4-5-20251001",
            max_tokens = 4096,
            messages   = new[] { new { role = "user", content = prompt } }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var res     = await Http.SendAsync(req);
        var resBody = await res.Content.ReadAsStringAsync();
        ctx.Logger.LogLine($"[sync] claude status: {res.StatusCode}");

        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogLine($"[sync] claude error body: {resBody}");
            return [];
        }

        var doc  = JsonDocument.Parse(resBody);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "[]";

        // Extract JSON array robustly
        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        if (start == -1 || end == -1 || end < start)
        {
            ctx.Logger.LogLine($"[sync] no JSON array in claude response: {text[..Math.Min(text.Length, 500)]}");
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<JsonElement>>(text[start..(end + 1)]) ?? [];
            return items.Select(ToDynamoItem).ToList();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[sync] JSON parse error: {ex.Message}");
            return [];
        }
    }

    private static Dictionary<string, AttributeValue> ToDynamoItem(JsonElement item)
    {
        var result = new Dictionary<string, AttributeValue>();
        foreach (var prop in item.EnumerateObject())
        {
            var val = prop.Value;
            if      (val.TryGetProperty("S",    out var s)) result[prop.Name] = new() { S    = s.GetString() };
            else if (val.TryGetProperty("N",    out var n)) result[prop.Name] = new() { N    = n.GetString() };
            else if (val.TryGetProperty("BOOL", out var b)) result[prop.Name] = new() { BOOL = b.GetBoolean() };
            else if (val.TryGetProperty("M",    out var m))
            {
                var map = new Dictionary<string, AttributeValue>();
                foreach (var mp in m.EnumerateObject())
                    if (mp.Value.TryGetProperty("N", out var mn))
                        map[mp.Name] = new() { N = mn.GetString() };
                result[prop.Name] = new() { M = map };
            }
        }
        return result;
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
