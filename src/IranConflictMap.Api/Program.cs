using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());
builder.Services.AddSingleton(new Amazon.Lambda.AmazonLambdaClient());

var app = builder.Build();

// ── In-memory cache ────────────────────────────────────────────────────────────
List<object>? strikeCache = null;
var strikeCacheExpiry = DateTime.MinValue;
List<object>? syncCache = null;
var syncCacheExpiry = DateTime.MinValue;

// ── GET /api/strikes ───────────────────────────────────────────────────────────
app.MapGet("/api/strikes", async (IAmazonDynamoDB dynamo) =>
{
    if (strikeCache is not null && DateTime.UtcNow < strikeCacheExpiry)
        return Results.Ok(strikeCache);

    var tableName = Environment.GetEnvironmentVariable("STRIKES_TABLE") ?? "strikes";
    var indexName = Environment.GetEnvironmentVariable("STRIKES_GSI")   ?? "entity-date-index";

    var response = await dynamo.QueryAsync(new QueryRequest
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
    });

    strikeCache = response.Items.Select(item => (object)new
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
        }
    }).ToList();

    strikeCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    return Results.Ok(strikeCache);
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
        id              = item["id"].S,
        timestamp       = item["timestamp"].S,
        new_event_count = int.Parse(item["new_event_count"].N),
        has_edits       = item.ContainsKey("has_edits") && item["has_edits"].BOOL,
        status          = item["status"].S
    }).ToList();

    syncCacheExpiry = DateTime.UtcNow.AddMinutes(5);
    return Results.Ok(syncCache);
});

// ── POST /api/sync/trigger ─────────────────────────────────────────────────────
app.MapPost("/api/sync/trigger", async () =>
{
    var functionName = Environment.GetEnvironmentVariable("SYNC_FUNCTION_NAME");
    if (string.IsNullOrEmpty(functionName))
        return Results.Problem("SYNC_FUNCTION_NAME not configured");

    var lambdaClient = new Amazon.Lambda.AmazonLambdaClient();
    var response = await lambdaClient.InvokeAsync(new Amazon.Lambda.Model.InvokeRequest
    {
        FunctionName    = functionName,
        InvocationType  = "Event"  // async, fire-and-forget
    });

    return Results.Ok(new { triggered = true, status = (int)response.StatusCode });
});

app.Run();
