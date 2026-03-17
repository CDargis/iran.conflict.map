using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());
builder.Services.AddSingleton<IAmazonSimpleSystemsManagement>(new AmazonSimpleSystemsManagementClient());
builder.Services.AddSingleton<IAmazonSQS>(new AmazonSQSClient());
builder.Services.AddSingleton(new Amazon.Lambda.AmazonLambdaClient());

var app = builder.Build();

// ── In-memory cache ────────────────────────────────────────────────────────────
var strikeCache = new Dictionary<string, (List<object> data, DateTime expiry)>();
List<object>? syncCache = null;
var syncCacheExpiry = DateTime.MinValue;
const string AllDatesKey = "all";

// ── GET /api/strikes ───────────────────────────────────────────────────────────
app.MapGet("/api/strikes", async (IAmazonDynamoDB dynamo, string? date) =>
{
    var cacheKey = date ?? AllDatesKey;
    if (strikeCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.expiry)
        return Results.Ok(cached.data);

    var tableName = Environment.GetEnvironmentVariable("STRIKES_TABLE") ?? "strikes";
    var indexName = Environment.GetEnvironmentVariable("STRIKES_GSI")   ?? "entity-date-index";

    var query = date != null
        ? new QueryRequest
        {
            TableName                 = tableName,
            IndexName                 = indexName,
            KeyConditionExpression    = "entity = :e AND #d = :date",
            ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":e"]    = new AttributeValue { S = "strike" },
                [":date"] = new AttributeValue { S = date }
            }
        }
        : new QueryRequest
        {
            TableName                 = tableName,
            IndexName                 = indexName,
            KeyConditionExpression    = "entity = :e AND #d >= :from",
            ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":e"]    = new AttributeValue { S = "strike" },
                [":from"] = new AttributeValue { S = "2026-02-27" }
            }
        };

    var response = await dynamo.QueryAsync(query);

    var items = response.Items.Select(item => (object)new
    {
        id          = item["id"].S,
        date        = item["date"].S,
        title       = item["title"].S,
        location    = item["location"].S,
        lat         = double.Parse(item["lat"].N),
        lng         = double.Parse(item["lng"].N),
        type        = item["type"].S,
        target_type = item["target_type"].S,
        actor       = item["actor"].S,
        severity    = item.ContainsKey("severity") ? item["severity"].S : "low",
        disputed    = item.ContainsKey("disputed") && item["disputed"].BOOL,
        description = item["description"].S,
        casualties  = new
        {
            confirmed = int.Parse(item["casualties"].M["confirmed"].N),
            estimated = int.Parse(item["casualties"].M["estimated"].N),
        },
        source_url          = item.ContainsKey("source_url")          ? item["source_url"].S          : "",
        citations           = item.ContainsKey("citations")
            ? item["citations"].L.Select(c => c.S).Where(c => !string.IsNullOrEmpty(c)).ToList()
            : new List<string>(),
        created_at          = item.ContainsKey("created_at")          ? item["created_at"].S          : "",
        created_source_url  = item.ContainsKey("created_source_url")  ? item["created_source_url"].S  : "",
        update_log          = item.ContainsKey("update_log")
            ? item["update_log"].L.Select(e => new
              {
                  at         = e.M.ContainsKey("at")         ? e.M["at"].S         : "",
                  source_url = e.M.ContainsKey("source_url") ? e.M["source_url"].S : "",
                  fields     = e.M.ContainsKey("fields")     ? e.M["fields"].SS    : new List<string>()
              }).ToList<object>()
            : new List<object>()
    }).ToList();

    strikeCache[cacheKey] = (items, DateTime.UtcNow.AddMinutes(5));
    return Results.Ok(items);
});

// ── GET /api/syncs ─────────────────────────────────────────────────────────────
app.MapGet("/api/syncs", async (IAmazonDynamoDB dynamo) =>
{
    if (syncCache is not null && DateTime.UtcNow < syncCacheExpiry)
        return Results.Ok(syncCache);

    var tableName = Environment.GetEnvironmentVariable("SYNCS_TABLE") ?? "syncs";

    var response = await dynamo.QueryAsync(new QueryRequest
    {
        TableName                 = tableName,
        IndexName                 = "entity-timestamp-index",
        KeyConditionExpression    = "entity = :e",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":e"] = new AttributeValue { S = "sync" }
        },
        ScanIndexForward = false,
        Limit            = 10
    });

    syncCache = response.Items.Select(item => (object)new
    {
        id                = item["id"].S,
        timestamp         = item["timestamp"].S,
        status            = item["status"].S,
        new_event_count   = item.ContainsKey("new_event_count")  ? int.Parse(item["new_event_count"].N)  : 0,
        update_count      = item.ContainsKey("update_count")     ? int.Parse(item["update_count"].N)     : 0,
        dead_letter_count = item.ContainsKey("dead_letter_count")? int.Parse(item["dead_letter_count"].N): 0,
        review_count      = item.ContainsKey("review_count")     ? int.Parse(item["review_count"].N)     : 0,
        has_edits         = item.ContainsKey("has_edits")        && item["has_edits"].BOOL,
        last_synced       = item.ContainsKey("last_synced")      ? item["last_synced"].S                 : "",
        report_url        = item.ContainsKey("report_url")       ? item["report_url"].S                  : "",
        url_strategy      = item.ContainsKey("url_strategy")     ? item["url_strategy"].S                : "",
        error_message     = item.ContainsKey("error_message")    ? item["error_message"].S               : ""
    }).ToList();

    syncCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    return Results.Ok(syncCache);
});

// ── Auth helper ────────────────────────────────────────────────────────────────
async Task<bool> ValidateSyncKey(HttpContext ctx, IAmazonSimpleSystemsManagement ssm)
{
    var providedKey = ctx.Request.Headers["X-Sync-Key"].FirstOrDefault() ?? "";
    if (string.IsNullOrEmpty(providedKey)) return false;
    var ssmPrefix = Environment.GetEnvironmentVariable("SSM_PREFIX") ?? "/iran-conflict-map";
    try
    {
        var param = await ssm.GetParameterAsync(new GetParameterRequest
        {
            Name           = $"{ssmPrefix}/sync_key",
            WithDecryption = true
        });
        return string.Equals(providedKey, param.Parameter.Value, StringComparison.Ordinal);
    }
    catch { return false; }
}

// ── POST /api/sync/trigger — scan inbox via extract Lambda ─────────────────────
app.MapPost("/api/sync/trigger", async (HttpContext ctx, IAmazonSimpleSystemsManagement ssm) =>
{
    if (!await ValidateSyncKey(ctx, ssm))
        return Results.Unauthorized();

    var functionName = Environment.GetEnvironmentVariable("EXTRACT_FUNCTION_NAME");
    if (string.IsNullOrEmpty(functionName))
        return Results.Problem("EXTRACT_FUNCTION_NAME not configured");

    var lambdaClient = new Amazon.Lambda.AmazonLambdaClient();
    var response = await lambdaClient.InvokeAsync(new Amazon.Lambda.Model.InvokeRequest
    {
        FunctionName   = functionName,
        InvocationType = "Event"   // async, fire-and-forget
    });

    return Results.Ok(new { triggered = true, status = (int)response.StatusCode });
});

// ── POST /api/sync/submit-url — manually submit a report URL ───────────────────
app.MapPost("/api/sync/submit-url", async (HttpContext ctx, IAmazonSimpleSystemsManagement ssm, IAmazonSQS sqs) =>
{
    if (!await ValidateSyncKey(ctx, ssm))
        return Results.Unauthorized();

    string? url;
    try
    {
        var body = await JsonSerializer.DeserializeAsync<SubmitUrlRequest>(ctx.Request.Body);
        url = body?.Url;
    }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

    if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "url is required and must be an https URL" });

    var queueUrl = Environment.GetEnvironmentVariable("REPORT_QUEUE_URL");
    if (string.IsNullOrEmpty(queueUrl))
        return Results.Problem("REPORT_QUEUE_URL not configured");

    var messageBody = JsonSerializer.Serialize(new { url });

    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl       = queueUrl,
        MessageBody    = messageBody,
        MessageGroupId = "manual"
    });

    return Results.Ok(new { queued = true, url });
});

// ── GET /api/review — peek at review queue items ───────────────────────────────
app.MapGet("/api/review", async (HttpContext ctx, IAmazonSimpleSystemsManagement ssm, IAmazonSQS sqs) =>
{
    if (!await ValidateSyncKey(ctx, ssm))
        return Results.Unauthorized();

    var queueUrl = Environment.GetEnvironmentVariable("REVIEW_QUEUE_URL");
    if (string.IsNullOrEmpty(queueUrl))
        return Results.Problem("REVIEW_QUEUE_URL not configured");

    var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl            = queueUrl,
        MaxNumberOfMessages = 10,
        VisibilityTimeout   = 300   // 5 minutes to review and act
    });

    var items = response.Messages.Select(m =>
    {
        try
        {
            var parsed = JsonDocument.Parse(m.Body).RootElement;
            return (object)new
            {
                receiptHandle = m.ReceiptHandle,
                source_url    = parsed.TryGetProperty("source_url", out var su) ? su.GetString() : "",
                note          = parsed.TryGetProperty("item", out var it)
                    ? (it.TryGetProperty("note", out var n) ? n.GetString() : "")
                    : (parsed.TryGetProperty("note", out var n2) ? n2.GetString() : ""),
                as_new        = parsed.TryGetProperty("item", out var it2) && it2.TryGetProperty("as_new",    out var an) ? an : (JsonElement?)null,
                as_update     = parsed.TryGetProperty("item", out var it3) && it3.TryGetProperty("as_update", out var au) ? au : (JsonElement?)null
            };
        }
        catch
        {
            return (object)new { receiptHandle = m.ReceiptHandle, source_url = "", note = "parse error", as_new = (JsonElement?)null, as_update = (JsonElement?)null };
        }
    }).ToList();

    return Results.Ok(items);
});

// ── POST /api/review/resolve — approve or discard a review item ────────────────
app.MapPost("/api/review/resolve", async (HttpContext ctx, IAmazonSimpleSystemsManagement ssm, IAmazonSQS sqs, IAmazonDynamoDB dynamo) =>
{
    if (!await ValidateSyncKey(ctx, ssm))
        return Results.Unauthorized();

    ResolveRequest? req;
    try { req = await JsonSerializer.DeserializeAsync<ResolveRequest>(ctx.Request.Body); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

    if (req is null || string.IsNullOrEmpty(req.ReceiptHandle) || string.IsNullOrEmpty(req.Action))
        return Results.BadRequest(new { error = "receiptHandle and action are required" });

    if (req.Action is not ("new" or "update" or "discard"))
        return Results.BadRequest(new { error = "action must be new, update, or discard" });

    var reviewQueueUrl    = Environment.GetEnvironmentVariable("REVIEW_QUEUE_URL");
    var processorQueueUrl = Environment.GetEnvironmentVariable("PROCESSOR_QUEUE_URL");
    var syncsTable        = Environment.GetEnvironmentVariable("SYNCS_TABLE") ?? "syncs";

    if (string.IsNullOrEmpty(reviewQueueUrl))
        return Results.Problem("REVIEW_QUEUE_URL not configured");

    if (req.Action != "discard" && string.IsNullOrEmpty(processorQueueUrl))
        return Results.Problem("PROCESSOR_QUEUE_URL not configured");

    if (req.Action != "discard")
    {
        // Write initial "processing" sync record — processor will update it with final counts
        var runId = DateTime.UtcNow.ToString("o");
        await dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = syncsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["id"]                = new() { S = runId },
                ["entity"]            = new() { S = "sync" },
                ["timestamp"]         = new() { S = runId },
                ["status"]            = new() { S = "processing" },
                ["new_event_count"]   = new() { N = "0" },
                ["update_count"]      = new() { N = "0" },
                ["dead_letter_count"] = new() { N = "0" },
                ["review_count"]      = new() { N = "0" },
                ["report_url"]        = new() { S = req.SourceUrl ?? "" }
            }
        });

        // Build SyncEnvelope and send to processor
        string envelope;
        if (req.Action == "new" && req.AsNew.HasValue)
        {
            envelope = JsonSerializer.Serialize(new
            {
                source_url = req.SourceUrl ?? "",
                synced_at  = runId,
                @new       = new[] { req.AsNew.Value },
                updates    = (object?)null,
                ambiguous  = (object?)null
            });
        }
        else if (req.Action == "update" && req.AsUpdate.HasValue)
        {
            envelope = JsonSerializer.Serialize(new
            {
                source_url = req.SourceUrl ?? "",
                synced_at  = runId,
                @new       = (object?)null,
                updates    = new[] { req.AsUpdate.Value },
                ambiguous  = (object?)null
            });
        }
        else
        {
            return Results.BadRequest(new { error = $"as_{req.Action} payload is required for action '{req.Action}'" });
        }

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl       = processorQueueUrl,
            MessageBody    = envelope,
            MessageGroupId = "review"
        });
    }

    // Delete from review queue
    await sqs.DeleteMessageAsync(new DeleteMessageRequest
    {
        QueueUrl      = reviewQueueUrl,
        ReceiptHandle = req.ReceiptHandle
    });

    return Results.Ok(new { resolved = true, action = req.Action });
});

app.Run();

record SubmitUrlRequest([property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url);

record ResolveRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("receiptHandle")] string  ReceiptHandle,
    [property: System.Text.Json.Serialization.JsonPropertyName("action")]        string  Action,
    [property: System.Text.Json.Serialization.JsonPropertyName("source_url")]    string? SourceUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("as_new")]        System.Text.Json.JsonElement? AsNew,
    [property: System.Text.Json.Serialization.JsonPropertyName("as_update")]     System.Text.Json.JsonElement? AsUpdate
);
