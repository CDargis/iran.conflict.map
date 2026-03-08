using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());

var app = builder.Build();

List<object>? strikeCache = null;
var cacheExpiry = DateTime.MinValue;

app.MapGet("/api/strikes", async (IAmazonDynamoDB dynamo) =>
{
    if (strikeCache is not null && DateTime.UtcNow < cacheExpiry)
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

    cacheExpiry = DateTime.UtcNow.AddMinutes(5);

    return Results.Ok(strikeCache);
});

app.Run();
