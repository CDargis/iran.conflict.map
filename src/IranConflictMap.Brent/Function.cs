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
    private readonly IAmazonDynamoDB                     _dynamo;
    private readonly IAmazonSimpleSystemsManagement      _ssm;
    private readonly HttpClient                          _http;
    private readonly string                              _brentTableName;
    private readonly string                              _brentApiKeyParam;

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

        try
        {
            price = await FetchBrentPriceAsync(context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to fetch Brent price: {ex.Message}");
            return;   // do not throw — let EventBridge retry on next schedule
        }

        string fetchedAt = DateTime.UtcNow.ToString("o");
        string date      = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _brentTableName,
            Item      = new Dictionary<string, AttributeValue>
            {
                ["date"]        = new AttributeValue { S = date },
                ["timestamp"]   = new AttributeValue { S = fetchedAt },
                ["brent_price"] = new AttributeValue { N = price.ToString("F2") },
                ["currency"]    = new AttributeValue { S = "USD" },
            }
        });

        context.Logger.LogInformation($"Wrote Brent price ${price:F2} for {date}");
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

    private async Task<decimal> FetchBrentPriceAsync(ILambdaContext context)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://api.oilpriceapi.com/v1/prices/latest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        context.Logger.LogInformation($"Oil Price API response: {json}");

        OilPriceResponse parsed = JsonSerializer.Deserialize<OilPriceResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new InvalidOperationException("Null response from Oil Price API");

        if (parsed.Status != "success" || parsed.Data is null)
            throw new InvalidOperationException($"Oil Price API returned status '{parsed.Status}'");

        return parsed.Data.Price;
    }
}

record OilPriceResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("data")]   OilPriceData? Data
);

record OilPriceData(
    [property: JsonPropertyName("price")] decimal Price
);
