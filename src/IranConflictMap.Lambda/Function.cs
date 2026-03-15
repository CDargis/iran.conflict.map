using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Lambda;

public class Function
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonSQS _sqs;

    private static readonly string StrikesTable = Env("STRIKES_TABLE", "strikes");
    private static readonly string SyncsTable = Env("SYNCS_TABLE", "syncs");
    private static readonly string DeadLetterQueueUrl = Env("DEAD_LETTER_QUEUE_URL", "");
    private static readonly string StrikesGsi = Env("STRIKES_GSI", "entity-date-index");

    public Function()
    {
        _dynamo = new AmazonDynamoDBClient();
        _sqs = new AmazonSQSClient();
    }

    // For testing
    public Function(IAmazonDynamoDB dynamo, IAmazonSQS sqs)
    {
        _dynamo = dynamo;
        _sqs = sqs;
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            var runId = DateTime.UtcNow.ToString("o");
            context.Logger.LogLine($"[processor] handling message {record.MessageId}");

            try
            {
                var payload = JsonSerializer.Deserialize<SyncEnvelope>(record.Body)
                    ?? throw new Exception("Failed to deserialize SQS message body");

                var newCount = 0;
                var updateApplied = 0;
                var updateDeadLettered = 0;
                var ambiguousCount = 0;

                if (payload.New is { Count: > 0 })
                {
                    newCount = await ProcessNewEvents(payload.New, payload.SourceUrl, payload.SyncedAt, context);
                }

                if (payload.Updates is { Count: > 0 })
                {
                    (updateApplied, updateDeadLettered) = await ProcessUpdates(payload.Updates, payload.SourceUrl, payload.SyncedAt, context);
                }

                if (payload.Ambiguous is { Count: > 0 })
                {
                    ambiguousCount = await ProcessAmbiguous(payload.Ambiguous, context);
                }

                var totalDeadLettered = updateDeadLettered + ambiguousCount;
                var status = totalDeadLettered > 0 ? "partial" : "success";
                var errors = new List<string>();
                if (updateDeadLettered > 0) errors.Add($"{updateDeadLettered} updates dead-lettered");
                if (ambiguousCount > 0) errors.Add($"{ambiguousCount} ambiguous items dead-lettered");

                await WriteSyncRecord(runId, status, newCount, updateApplied, totalDeadLettered,
                    errors.Count > 0 ? string.Join("; ", errors) : null, context);
                context.Logger.LogLine($"[processor] done — new={newCount} updates={updateApplied} dead-lettered={totalDeadLettered}");
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[processor] error: {ex}");
                await WriteSyncRecord(runId, "error", 0, 0, 0, ex.Message, context);
                throw;
            }
        }
    }

    // ── New Events ──────────────────────────────────────────────────────────────

    private async Task<int> ProcessNewEvents(List<NewEvent> newEvents, string? sourceUrl, string? syncedAt, ILambdaContext context)
    {
        var nextId = await GetNextId();
        var items = new List<Dictionary<string, AttributeValue>>();

        foreach (var evt in newEvents)
        {
            var item = ToDynamoItem(evt.PutRequest.Item);

            // Override the ID with our auto-incremented value
            item["id"] = new AttributeValue { S = nextId.ToString() };
            nextId++;

            // Audit fields
            if (!string.IsNullOrEmpty(syncedAt))
                item["created_at"] = new AttributeValue { S = syncedAt };
            if (!string.IsNullOrEmpty(sourceUrl))
                item["created_source_url"] = new AttributeValue { S = sourceUrl };

            items.Add(item);
        }

        var firstId = nextId - items.Count;

        // BatchWriteItem in chunks of 25; retry unprocessed items
        for (var i = 0; i < items.Count; i += 25)
        {
            var batch = items.Skip(i).Take(25)
                .Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } })
                .ToList();

            var remaining = new Dictionary<string, List<WriteRequest>> { [StrikesTable] = batch };
            var attempts = 0;
            while (remaining.Count > 0 && attempts++ < 5)
            {
                var resp = await _dynamo.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = remaining });
                remaining = resp.UnprocessedItems;
                if (remaining.Count > 0)
                    await Task.Delay(200 * attempts);
            }

            if (remaining.Count > 0)
                context.Logger.LogLine($"[processor] warning: {remaining.Values.Sum(r => r.Count)} items still unprocessed after retries");
        }

        context.Logger.LogLine($"[processor] wrote {items.Count} new events (IDs {firstId}..{nextId - 1})");
        return items.Count;
    }

    // ── Updates ─────────────────────────────────────────────────────────────────

    private async Task<(int applied, int deadLettered)> ProcessUpdates(List<UpdateEvent> updates, string? sourceUrl, string? syncedAt, ILambdaContext context)
    {
        var applied = 0;
        var deadLettered = 0;

        foreach (var update in updates)
        {
            var lookup = update.Lookup;
            string? existingId = null;

            // If lookup has an explicit id, use it directly
            if (lookup.TryGetValue("id", out var idVal) && idVal is JsonElement idEl)
            {
                existingId = idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : idEl.GetRawText();
            }

            if (existingId != null)
            {
                // Direct lookup by id
                var getResp = await _dynamo.GetItemAsync(new GetItemRequest
                {
                    TableName = StrikesTable,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = new AttributeValue { S = existingId }
                    }
                });

                if (getResp.Item == null || getResp.Item.Count == 0)
                {
                    context.Logger.LogLine($"[processor] update: id={existingId} not found, dead-lettering");
                    await SendToDeadLetter("update_not_found", JsonSerializer.Serialize(update));
                    deadLettered++;
                    continue;
                }

                await ApplyUpdate(existingId, getResp.Item, update.Changes, sourceUrl, syncedAt, context);
                applied++;
            }
            else
            {
                // Query by date + location + actor via entity GSI
                var date = GetLookupString(lookup, "date");
                var location = GetLookupString(lookup, "location");
                var actor = GetLookupString(lookup, "actor");

                if (date == null || location == null || actor == null)
                {
                    context.Logger.LogLine($"[processor] update missing lookup fields, dead-lettering");
                    await SendToDeadLetter("update_missing_lookup", JsonSerializer.Serialize(update));
                    deadLettered++;
                    continue;
                }

                var queryResp = await _dynamo.QueryAsync(new QueryRequest
                {
                    TableName = StrikesTable,
                    IndexName = StrikesGsi,
                    KeyConditionExpression = "entity = :entity AND #d = :date",
                    FilterExpression = "#loc = :location AND actor = :actor",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#d"] = "date",
                        ["#loc"] = "location"
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":entity"] = new AttributeValue { S = "strike" },
                        [":date"] = new AttributeValue { S = date },
                        [":location"] = new AttributeValue { S = location },
                        [":actor"] = new AttributeValue { S = actor }
                    }
                });

                if (queryResp.Items.Count == 0)
                {
                    context.Logger.LogLine($"[processor] update: no match for {date}/{location}/{actor}, dead-lettering");
                    await SendToDeadLetter("update_no_match", JsonSerializer.Serialize(update));
                    deadLettered++;
                    continue;
                }

                if (queryResp.Items.Count > 1)
                {
                    context.Logger.LogLine($"[processor] update: {queryResp.Items.Count} matches for {date}/{location}/{actor}, dead-lettering");
                    await SendToDeadLetter("update_multiple_matches", JsonSerializer.Serialize(update));
                    deadLettered++;
                    continue;
                }

                var existing = queryResp.Items[0];
                existingId = existing["id"].S;
                await ApplyUpdate(existingId, existing, update.Changes, sourceUrl, syncedAt, context);
                applied++;
            }
        }

        return (applied, deadLettered);
    }

    private async Task ApplyUpdate(string id, Dictionary<string, AttributeValue> existing,
        Dictionary<string, JsonElement> changes, string? sourceUrl, string? syncedAt, ILambdaContext context)
    {
        var updates = new Dictionary<string, AttributeValueUpdate>();

        foreach (var (field, value) in changes)
        {
            // Skip lookup fields — they're not changes
            if (field is "date" or "location" or "actor" or "id") continue;

            if (field == "citations")
            {
                // Union: merge incoming citations with existing
                var existingCitations = new HashSet<string>();
                if (existing.TryGetValue("citations", out var existingList) && existingList.L != null)
                {
                    foreach (var c in existingList.L)
                        existingCitations.Add(c.S);
                }

                var incomingAttr = ToDynamoAttributeValue(value);
                if (incomingAttr.L != null)
                {
                    foreach (var c in incomingAttr.L)
                        existingCitations.Add(c.S);
                }

                updates[field] = new AttributeValueUpdate
                {
                    Action = AttributeAction.PUT,
                    Value = new AttributeValue
                    {
                        L = existingCitations.Select(u => new AttributeValue { S = u }).ToList()
                    }
                };
            }
            else
            {
                updates[field] = new AttributeValueUpdate
                {
                    Action = AttributeAction.PUT,
                    Value = ToDynamoAttributeValue(value)
                };
            }
        }

        // Append audit entry to update_log
        if (!string.IsNullOrEmpty(syncedAt))
        {
            var logEntry = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    ["at"]     = new AttributeValue { S = syncedAt },
                    ["fields"] = new AttributeValue { SS = updates.Keys.ToList() }
                }
            };
            if (!string.IsNullOrEmpty(sourceUrl))
                logEntry.M["source_url"] = new AttributeValue { S = sourceUrl };

            var existingLog = existing.TryGetValue("update_log", out var ul) && ul.L != null
                ? ul.L.ToList()
                : new List<AttributeValue>();
            existingLog.Add(logEntry);
            updates["update_log"] = new AttributeValueUpdate
            {
                Action = AttributeAction.PUT,
                Value  = new AttributeValue { L = existingLog }
            };
        }

        if (updates.Count > 0)
        {
            await _dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = StrikesTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = id }
                },
                AttributeUpdates = updates
            });
            context.Logger.LogLine($"[processor] updated id={id}, fields=[{string.Join(", ", updates.Keys)}]");
        }
    }

    // ── Ambiguous ───────────────────────────────────────────────────────────────

    private async Task<int> ProcessAmbiguous(List<JsonElement> ambiguous, ILambdaContext context)
    {
        foreach (var item in ambiguous)
        {
            await SendToDeadLetter("ambiguous", item.GetRawText());
            context.Logger.LogLine($"[processor] sent ambiguous item to dead-letter");
        }
        return ambiguous.Count;
    }

    // ── DynamoDB Helpers ────────────────────────────────────────────────────────

    private async Task<int> GetNextId()
    {
        var response = await _dynamo.ScanAsync(new ScanRequest
        {
            TableName = StrikesTable,
            ProjectionExpression = "id"
        });

        return response.Items
            .Select(i => int.TryParse(i["id"].S, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private async Task WriteSyncRecord(string id, string status, int newCount, int updateCount,
        int deadLetterCount, string? errorMessage, ILambdaContext context)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["id"] = new() { S = id },
            ["entity"] = new() { S = "sync" },
            ["timestamp"] = new() { S = id },
            ["status"] = new() { S = status },
            ["new_event_count"] = new() { N = newCount.ToString() },
            ["update_count"] = new() { N = updateCount.ToString() },
            ["dead_letter_count"] = new() { N = deadLetterCount.ToString() }
        };

        if (errorMessage != null)
            item["error_message"] = new AttributeValue { S = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage };

        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = SyncsTable,
            Item = item
        });
    }

    // ── SQS Helpers ─────────────────────────────────────────────────────────────

    private async Task SendToDeadLetter(string reason, string body)
    {
        if (string.IsNullOrEmpty(DeadLetterQueueUrl)) return;

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = DeadLetterQueueUrl,
            MessageBody = body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["reason"] = new() { DataType = "String", StringValue = reason }
            }
        });
    }

    // ── JSON → DynamoDB Conversion ──────────────────────────────────────────────

    private static Dictionary<string, AttributeValue> ToDynamoItem(Dictionary<string, JsonElement> item)
    {
        var result = new Dictionary<string, AttributeValue>();
        foreach (var (key, value) in item)
        {
            var attr = ToDynamoAttributeValue(value);
            if (attr != null) result[key] = attr;
        }
        return result;
    }

    // Returns null for empty/null string values so callers can skip them.
    private static AttributeValue? ToDynamoAttributeValue(JsonElement element)
    {
        // DynamoDB wire format: { "S": "..." }, { "N": "..." }, { "BOOL": true }, { "M": {...} }, { "L": [...] }
        if (element.TryGetProperty("S", out var s))
        {
            var str = s.GetString();
            return string.IsNullOrEmpty(str) ? null : new AttributeValue { S = str };
        }

        if (element.TryGetProperty("N", out var n))
        {
            var num = n.GetString();
            return string.IsNullOrEmpty(num) ? null : new AttributeValue { N = num };
        }

        if (element.TryGetProperty("BOOL", out var b))
            return new AttributeValue { BOOL = b.GetBoolean() };

        if (element.TryGetProperty("M", out var m))
        {
            var map = new Dictionary<string, AttributeValue>();
            foreach (var prop in m.EnumerateObject())
            {
                var attr = ToDynamoAttributeValue(prop.Value);
                if (attr != null) map[prop.Name] = attr;
            }
            return new AttributeValue { M = map };
        }

        if (element.TryGetProperty("L", out var l))
        {
            var list = l.EnumerateArray()
                .Select(ToDynamoAttributeValue)
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();
            return list.Count == 0 ? null : new AttributeValue { L = list };
        }

        throw new Exception($"Unsupported DynamoDB attribute type: {element.GetRawText()}");
    }

    private static string? GetLookupString(Dictionary<string, JsonElement> lookup, string key)
    {
        if (lookup.TryGetValue(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.TryGetProperty("S", out var s)) return s.GetString();
        }
        return null;
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

// ── Message DTOs ────────────────────────────────────────────────────────────

public class SyncEnvelope
{
    [System.Text.Json.Serialization.JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("synced_at")]
    public string? SyncedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("new")]
    public List<NewEvent>? New { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updates")]
    public List<UpdateEvent>? Updates { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("ambiguous")]
    public List<JsonElement>? Ambiguous { get; set; }
}

public class NewEvent
{
    public PutRequestWrapper PutRequest { get; set; } = new();
}

public class PutRequestWrapper
{
    public Dictionary<string, JsonElement> Item { get; set; } = new();
}

public class UpdateEvent
{
    [System.Text.Json.Serialization.JsonPropertyName("lookup")]
    public Dictionary<string, JsonElement> Lookup { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("changes")]
    public Dictionary<string, JsonElement> Changes { get; set; } = new();
}
