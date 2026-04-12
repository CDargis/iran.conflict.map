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
List<string>? strikeDatesCache = null;
var strikeDatesCacheExpiry = DateTime.MinValue;
List<object>? syncCache = null;
var syncCacheExpiry = DateTime.MinValue;
var signalsCache = new Dictionary<string, (object data, DateTime expiry)>();

// ── GET /api/strikes ───────────────────────────────────────────────────────────
app.MapGet("/api/strikes", async (IAmazonDynamoDB dynamo, string? date) =>
{
    string cacheKey = date ?? "all";
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
        notes               = item.ContainsKey("notes")
            ? item["notes"].L.Select(n => n.S).Where(n => !string.IsNullOrEmpty(n)).ToList()
            : new List<string>(),
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

// ── GET /api/strikes/dates ─────────────────────────────────────────────────────
app.MapGet("/api/strikes/dates", async (IAmazonDynamoDB dynamo) =>
{
    if (strikeDatesCache is not null && DateTime.UtcNow < strikeDatesCacheExpiry)
        return Results.Ok(strikeDatesCache);

    string strikesTable = Environment.GetEnvironmentVariable("STRIKES_TABLE") ?? "strikes";
    string strikesIndex = Environment.GetEnvironmentVariable("STRIKES_GSI")   ?? "entity-date-index";
    string syncsTable   = Environment.GetEnvironmentVariable("SYNCS_TABLE")   ?? "syncs";

    Task<QueryResponse> strikesTask = dynamo.QueryAsync(new QueryRequest
    {
        TableName                 = strikesTable,
        IndexName                 = strikesIndex,
        KeyConditionExpression    = "entity = :e AND #d >= :from",
        ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":e"]    = new AttributeValue { S = "strike" },
            [":from"] = new AttributeValue { S = "2026-02-27" }
        },
        ProjectionExpression = "#d",
    });

    // Include dates from successful syncs so days with no events still appear in the date picker
    Task<QueryResponse> syncsTask = dynamo.QueryAsync(new QueryRequest
    {
        TableName                 = syncsTable,
        IndexName                 = "entity-run-index",
        KeyConditionExpression    = "entity = :e AND run_id >= :from",
        FilterExpression          = "#s = :success",
        ExpressionAttributeNames  = new Dictionary<string, string> { ["#s"] = "status" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":e"]       = new AttributeValue { S = "sync" },
            [":from"]    = new AttributeValue { S = "2026-02-27" },
            [":success"] = new AttributeValue { S = "success" }
        },
        ProjectionExpression = "run_id",
    });

    await Task.WhenAll(strikesTask, syncsTask);

    IEnumerable<string> strikeDates = strikesTask.Result.Items.Select(item => item["date"].S);
    IEnumerable<string> syncDates   = syncsTask.Result.Items
        .Where(item => item.ContainsKey("run_id") && item["run_id"].S.Length >= 10)
        .Select(item => item["run_id"].S[..10]);

    List<string> dates = strikeDates
        .Concat(syncDates)
        .Distinct()
        .OrderBy(d => d)
        .ToList();

    strikeDatesCache = dates;
    strikeDatesCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    return Results.Ok(dates);
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
        IndexName                 = "entity-run-index",
        KeyConditionExpression    = "entity = :e",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":e"] = new AttributeValue { S = "sync" }
        },
        ScanIndexForward = false,
        Limit            = 50
    });

    static object MapRun(Dictionary<string, AttributeValue> item) => new
    {
        run_id            = item.ContainsKey("run_id")           ? item["run_id"].S                      : "",
        status            = item.ContainsKey("status")           ? item["status"].S                      : "",
        new_event_count   = item.ContainsKey("new_event_count")  ? int.Parse(item["new_event_count"].N)  : 0,
        update_count      = item.ContainsKey("update_count")     ? int.Parse(item["update_count"].N)     : 0,
        dead_letter_count = item.ContainsKey("dead_letter_count")? int.Parse(item["dead_letter_count"].N): 0,
        review_count      = item.ContainsKey("review_count")     ? int.Parse(item["review_count"].N)     : 0,
        sync_duration_ms  = item.ContainsKey("sync_duration_ms")  ? long.Parse(item["sync_duration_ms"].N)  : (long?)null,
        total_duration_ms = item.ContainsKey("total_duration_ms") ? long.Parse(item["total_duration_ms"].N) : (long?)null,
        url_strategy      = item.ContainsKey("url_strategy")     ? item["url_strategy"].S                : "",
        error_message     = item.ContainsKey("error_message")    ? item["error_message"].S               : "",
        sync_notes        = item.ContainsKey("sync_notes") && item["sync_notes"].L != null
                            ? item["sync_notes"].L.Select(n => n.S).ToList()
                            : null
    };

    syncCache = response.Items
        .GroupBy(item => item.ContainsKey("report_url") ? item["report_url"].S : "unknown")
        .Take(10)
        .Select(g => (object)new
        {
            url  = g.Key,
            runs = g.Select(MapRun).ToList()
        })
        .ToList();

    syncCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    return Results.Ok(syncCache);
});

// ── GET /api/economic/brent ────────────────────────────────────────────────────
// Returns time-series Brent price rows. Historical dates: one row each (latest
// timestamp). Today: all accumulated intraday readings.
app.MapGet("/api/economic/brent", async (IAmazonDynamoDB dynamo, string? from, string? to) =>
{
    string tableName = Environment.GetEnvironmentVariable("BRENT_TABLE_NAME") ?? "iran-conflict-map-brent-prices";
    string fromDate  = from ?? "2026-02-14";
    string toDate    = to   ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
    string today     = DateTime.UtcNow.ToString("yyyy-MM-dd");

    var scanRequest = new ScanRequest
    {
        TableName                 = tableName,
        FilterExpression          = "#d BETWEEN :from AND :to",
        ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":from"] = new AttributeValue { S = fromDate },
            [":to"]   = new AttributeValue { S = toDate   },
        }
    };

    var scanResponse = await dynamo.ScanAsync(scanRequest);

    // Group by date. Historical: keep only the row with the latest timestamp per date.
    // Today: return all rows accumulated so far (intraday resolution).
    var rows = scanResponse.Items
        .GroupBy(item => item["date"].S)
        .SelectMany(g =>
        {
            if (g.Key == today)
                return (IEnumerable<Dictionary<string, AttributeValue>>)g.OrderBy(item => item["timestamp"].S);

            // Historical: latest timestamp only
            return new[] { g.OrderByDescending(item => item["timestamp"].S).First() };
        })
        .OrderBy(item => item["date"].S)
        .ThenBy(item => item["timestamp"].S)
        .Select(item => (object)MapBrentItem(item))
        .ToList();

    return Results.Ok(rows);
});

static object MapBrentItem(Dictionary<string, AttributeValue> item) => new
{
    date        = item["date"].S,
    timestamp   = item["timestamp"].S,
    brent_price = double.Parse(item["brent_price"].N),
};

// ── GET /api/economic/signals?date=YYYY-MM-DD ──────────────────────────────────
// Returns economic signals for a specific date. If no row exists for that date,
// falls back to the most recent row before it via GSI (for sticky Hormuz status).
// Response: { date, hormuz_status, economic_notes, source_url } or 404.
app.MapGet("/api/economic/signals", async (IAmazonDynamoDB dynamo, string? date) =>
{
    if (string.IsNullOrEmpty(date))
        return Results.BadRequest(new { error = "date query parameter is required (YYYY-MM-DD)" });

    if (signalsCache.TryGetValue(date, out var cached) && DateTime.UtcNow < cached.expiry)
        return Results.Ok(cached.data);

    string tableName = Environment.GetEnvironmentVariable("SIGNALS_TABLE_NAME") ?? "iran-conflict-map-economic-signals";

    // Query table by PK for full record (includes economic_notes)
    QueryResponse tableResp = await dynamo.QueryAsync(new QueryRequest
    {
        TableName                 = tableName,
        KeyConditionExpression    = "#d = :date",
        ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":date"] = new AttributeValue { S = date }
        },
        Limit = 1
    });

    // Extract notes from exact row if present; skip to sticky if status is no_alert (report was silent)
    List<string>? exactNotes = null;
    if (tableResp.Items.Count > 0)
    {
        Dictionary<string, AttributeValue> exactItem = tableResp.Items[0];
        string exactStatus = exactItem.ContainsKey("hormuz_status") ? exactItem["hormuz_status"].S : "no_alert";
        exactNotes = exactItem.ContainsKey("economic_notes")
            ? exactItem["economic_notes"].L.Select(n => n.S).Where(s => s != null).ToList()
            : null;

        if (exactStatus != "no_alert")
        {
            object directResult = MapSignalItem(exactItem);
            signalsCache[date] = (directResult, DateTime.UtcNow.AddMinutes(5));
            return Results.Ok(directResult);
        }
    }

    // No row, or row was silent on Hormuz — query GSI for most recent known status
    QueryResponse gsiResp = await dynamo.QueryAsync(new QueryRequest
    {
        TableName                 = tableName,
        IndexName                 = "entity-date-index",
        KeyConditionExpression    = "entity = :e AND #d <= :date",
        ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":e"]    = new AttributeValue { S = "signal" },
            [":date"] = new AttributeValue { S = date }
        },
        ScanIndexForward = false,
        Limit            = 20
    });

    // Find the most recent row with a real status (skip no_alert — those were silent reports)
    Dictionary<string, AttributeValue>? stickyItem = gsiResp.Items
        .FirstOrDefault(i => i.ContainsKey("hormuz_status") && i["hormuz_status"].S != "no_alert");

    if (stickyItem != null)
    {
        string stickyDate = stickyItem.ContainsKey("date") ? stickyItem["date"].S : date;
        object stickyResult = new
        {
            date           = date,
            source_url     = stickyItem.ContainsKey("source_url") ? stickyItem["source_url"].S : "",
            hormuz_status  = stickyItem["hormuz_status"].S,
            economic_notes = exactNotes,    // notes from exact row if we had one; null otherwise
            is_fallback    = true,
            hormuz_as_of   = stickyDate
        };
        signalsCache[date] = (stickyResult, DateTime.UtcNow.AddMinutes(5));
        return Results.Ok(stickyResult);
    }

    object defaultResult = new { date, hormuz_status = "no_alert", economic_notes = exactNotes, is_fallback = false };
    signalsCache[date] = (defaultResult, DateTime.UtcNow.AddMinutes(5));
    return Results.Ok(defaultResult);
});

static object MapSignalItem(Dictionary<string, AttributeValue> item) => new
{
    date           = item.ContainsKey("date")          ? item["date"].S          : "",
    source_url     = item.ContainsKey("source_url")    ? item["source_url"].S    : "",
    hormuz_status  = item.ContainsKey("hormuz_status") ? item["hormuz_status"].S : "no_alert",
    economic_notes = item.ContainsKey("economic_notes")
        ? item["economic_notes"].L.Select(n => n.S).Where(s => s != null).ToList()
        : null,
    is_fallback    = false
};

// ── URL normalization ──────────────────────────────────────────────────────────
static string NormalizeReportUrl(string url)
{
    var uri = new Uri(url.Trim());
    return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()}";
}

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

    url = NormalizeReportUrl(url);
    var messageBody = JsonSerializer.Serialize(new { run_id = DateTime.UtcNow.ToString("o"), url });

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
            var parsed     = JsonDocument.Parse(m.Body).RootElement;
            var hasWrapper = parsed.TryGetProperty("item", out JsonElement data);
            if (!hasWrapper) data = parsed;  // unwrapped format: item fields at root

            return (object)new
            {
                receiptHandle   = m.ReceiptHandle,
                source_url      = parsed.TryGetProperty("source_url", out var su) ? su.GetString() : "",
                sync_id         = parsed.TryGetProperty("sync_id",    out var si) ? si.GetString() : null,
                note            = data.TryGetProperty("note", out var n) ? n.GetString() : "",
                existing_id     = (data.TryGetProperty("existing_id",     out var ei) && ei.ValueKind != JsonValueKind.Null) ? (object?)ei : null,
                existing_record = (data.TryGetProperty("existing_record", out var er) && er.ValueKind != JsonValueKind.Null) ? (object?)er : null,
                nearest_record       = (data.TryGetProperty("nearest_record",       out var nr) && nr.ValueKind != JsonValueKind.Null) ? (object?)nr : null,
                proximity_ambiguous  = (data.TryGetProperty("proximity_ambiguous",  out var pa) && pa.ValueKind == JsonValueKind.True),
                as_new               = (data.TryGetProperty("as_new",    out var an) && an.ValueKind != JsonValueKind.Null) ? (object?)an : null,
                as_update            = (data.TryGetProperty("as_update", out var au) && au.ValueKind != JsonValueKind.Null) ? (object?)au : null
            };
        }
        catch
        {
            return (object)new { receiptHandle = m.ReceiptHandle, source_url = (string?)"", note = (string?)"parse error", as_new = (object?)null, as_update = (object?)null };
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
        // If no syncId, this is a manual approval without an origin sync — create a new record.
        // If syncId is present, the processor will skip UpdateSyncRecord (is_review_approval=true).
        var runId = req.SyncId ?? DateTime.UtcNow.ToString("o");
        if (req.SyncId == null)
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = syncsTable,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["report_url"]        = new() { S = req.SourceUrl ?? "manual" },
                    ["run_id"]            = new() { S = runId },
                    ["entity"]            = new() { S = "sync" },
                    ["status"]            = new() { S = "processing" },
                    ["new_event_count"]   = new() { N = "0" },
                    ["update_count"]      = new() { N = "0" },
                    ["dead_letter_count"] = new() { N = "0" },
                    ["review_count"]      = new() { N = "0" }
                }
            });
        }

        // Build SyncEnvelope and send to processor
        string envelope;
        if (req.Action == "new" && req.AsNew.HasValue)
        {
            envelope = JsonSerializer.Serialize(new
            {
                source_url         = req.SourceUrl ?? "",
                synced_at          = runId,
                is_review_approval = req.SyncId != null,
                @new               = new[] { StampNewEventId(req.AsNew.Value) },
                updates            = (object?)null,
                ambiguous          = (object?)null
            });
        }
        else if (req.Action == "update" && req.AsUpdate.HasValue)
        {
            envelope = JsonSerializer.Serialize(new
            {
                source_url         = req.SourceUrl ?? "",
                synced_at          = runId,
                is_review_approval = req.SyncId != null,
                @new               = (object?)null,
                updates            = new[] { req.AsUpdate.Value },
                ambiguous          = (object?)null
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

// Stamps a fresh GUID onto the Item of a PutRequest/Item JsonElement if id is absent.
static JsonElement StampNewEventId(JsonElement putRequest)
{
    if (!putRequest.TryGetProperty("PutRequest", out JsonElement pr) ||
        !pr.TryGetProperty("Item", out JsonElement item))
        return putRequest;

    // If already has an id, return as-is
    if (item.TryGetProperty("id", out _))
        return putRequest;

    using System.IO.MemoryStream ms = new();
    using System.Text.Json.Utf8JsonWriter writer = new(ms);
    writer.WriteStartObject();                     // PutRequest wrapper
    writer.WritePropertyName("PutRequest");
    writer.WriteStartObject();
    writer.WritePropertyName("Item");
    writer.WriteStartObject();
    writer.WritePropertyName("id");
    writer.WriteStartObject(); writer.WriteString("S", Guid.NewGuid().ToString()); writer.WriteEndObject();
    foreach (System.Text.Json.JsonProperty prop in item.EnumerateObject())
    {
        writer.WritePropertyName(prop.Name);
        prop.Value.WriteTo(writer);
    }
    writer.WriteEndObject();                       // Item
    writer.WriteEndObject();                       // PutRequest
    writer.WriteEndObject();
    writer.Flush();
    return JsonDocument.Parse(ms.ToArray()).RootElement;
}

record SubmitUrlRequest([property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url);

record ResolveRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("receiptHandle")] string  ReceiptHandle,
    [property: System.Text.Json.Serialization.JsonPropertyName("action")]        string  Action,
    [property: System.Text.Json.Serialization.JsonPropertyName("source_url")]    string? SourceUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("sync_id")]       string? SyncId,
    [property: System.Text.Json.Serialization.JsonPropertyName("as_new")]        System.Text.Json.JsonElement? AsNew,
    [property: System.Text.Json.Serialization.JsonPropertyName("as_update")]     System.Text.Json.JsonElement? AsUpdate
);
