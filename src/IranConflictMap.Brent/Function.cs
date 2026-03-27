using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Brent;

public class Function
{
    private readonly IAmazonDynamoDB          _dynamo;
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly HttpClient               _http;
    private readonly string                   _brentTableName;
    private readonly string                   _brentApiKeyParam;

    // API key is fetched once per cold start and cached for the Lambda lifetime.
    private string? _apiKey;

    public Function()
    {
        _dynamo           = new AmazonDynamoDBClient();
        _ssm              = new AmazonSimpleSystemsManagementClient();
        _http             = new HttpClient();
        _brentTableName   = Environment.GetEnvironmentVariable("BRENT_TABLE_NAME")    ?? "iran-conflict-map-brent-prices";
        _brentApiKeyParam = Environment.GetEnvironmentVariable("BRENT_API_KEY_PARAM") ?? "/iran-conflict-map/brent_api_key";
    }

    public async Task FunctionHandler(object input, ILambdaContext context)
    {
        context.Logger.LogInformation("Brent price Lambda invoked");

        _apiKey ??= await FetchApiKeyAsync(context);
        if (_apiKey is null)
        {
            context.Logger.LogError("Brent API key not found in SSM — aborting");
            return;
        }

        decimal price;
        string  createdAt;
        List<BrentPrediction> predictions;

        try
        {
            (price, createdAt, predictions) = await FetchBrentPriceAsync(context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to fetch Brent price: {ex.Message}");
            return;   // do not throw — let EventBridge retry on next schedule
        }

        string fetchedAt = DateTime.UtcNow.ToString("o");
        string date      = DateTime.UtcNow.ToString("yyyy-MM-dd");

        Dictionary<string, AttributeValue> item = new()
        {
            ["date"]        = new AttributeValue { S = date },
            ["timestamp"]   = new AttributeValue { S = fetchedAt },
            ["brent_price"] = new AttributeValue { N = price.ToString("F2") },
            ["currency"]    = new AttributeValue { S = "USD" },
        };

        if (predictions.Count > 0)
        {
            item["predictions"] = new AttributeValue
            {
                L = predictions.Select(p => new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["period"] = new AttributeValue { S = p.Period },
                        ["value"]  = new AttributeValue { N = p.Value  },
                        ["unit"]   = new AttributeValue { S = p.Unit   },
                    }
                }).ToList()
            };
        }

        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _brentTableName,
            Item      = item,
        });

        context.Logger.LogInformation($"Wrote Brent price ${price:F2} for {date} (fetched_at: {createdAt})");
    }

    private async Task<string?> FetchApiKeyAsync(ILambdaContext context)
    {
        try
        {
            GetParameterResponse response = await _ssm.GetParameterAsync(new GetParameterRequest
            {
                Name           = _brentApiKeyParam,
                WithDecryption = true,
            });
            return response.Parameter.Value;
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"SSM GetParameter failed for {_brentApiKeyParam}: {ex.Message}");
            return null;
        }
    }

    private async Task<(decimal price, string createdAt, List<BrentPrediction> predictions)> FetchBrentPriceAsync(ILambdaContext context)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://www.crudepriceapi.com/api/prices/latest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        context.Logger.LogInformation($"Crude Price API response: {json}");

        CrudePriceResponse parsed = JsonSerializer.Deserialize<CrudePriceResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new InvalidOperationException("Null response from Crude Price API");

        if (parsed.Status != "success" || parsed.Data is null)
            throw new InvalidOperationException($"Crude Price API returned status '{parsed.Status}'");

        decimal price = decimal.Parse(parsed.Data.Price);

        List<BrentPrediction> predictions = parsed.Data.NextTwoMonthsPredictions?
            .Select(p => new BrentPrediction(p.Period, p.Value, p.Unit))
            .ToList() ?? new List<BrentPrediction>();

        return (price, parsed.Data.CreatedAt, predictions);
    }
}

// ── Response models (matches crudepriceapi.com shape) ─────────────────────────
record CrudePriceResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("data")]   CrudePriceData? Data
);

record CrudePriceData(
    [property: JsonPropertyName("price")]                      string Price,
    [property: JsonPropertyName("currency")]                   string Currency,
    [property: JsonPropertyName("created_at")]                 string CreatedAt,
    [property: JsonPropertyName("next_two_months_predictions")] List<CrudePricePredictionRaw>? NextTwoMonthsPredictions
);

record CrudePricePredictionRaw(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("value")]  string Value,
    [property: JsonPropertyName("unit")]   string Unit
);

// Internal clean model (all strings to match DynamoDB N type storage)
record BrentPrediction(string Period, string Value, string Unit);
