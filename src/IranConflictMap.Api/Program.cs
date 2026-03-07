using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());

var app = builder.Build();

app.MapGet("/api/strikes", async (IAmazonDynamoDB dynamo) =>
{
    var tableName = Environment.GetEnvironmentVariable("STRIKES_TABLE") ?? "strikes";

    var response = await dynamo.ScanAsync(new ScanRequest { TableName = tableName });

    var strikes = response.Items.Select(item => new
    {
        id           = item["id"].S,
        date         = item["date"].S,
        title        = item["title"].S,
        location     = item["location"].S,
        lat          = double.Parse(item["lat"].N),
        lng          = double.Parse(item["lng"].N),
        type         = item["type"].S,
        target_type  = item["target_type"].S,
        actor        = item["actor"].S,
        severity     = item.ContainsKey("severity") ? item["severity"].S : "low",
        description  = item["description"].S,
        casualties   = new
        {
            confirmed = int.Parse(item["casualties"].M["confirmed"].N),
            estimated = int.Parse(item["casualties"].M["estimated"].N),
        }
    });

    return Results.Ok(strikes);
});

app.Run();
