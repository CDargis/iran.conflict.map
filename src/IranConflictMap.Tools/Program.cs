using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Text.Json;

// ── Global flags ─────────────────────────────────────────────────────────────
var profile  = GetFlag(ref args, "--profile") ?? "default";
var region   = GetFlag(ref args, "--region")  ?? "us-east-1";

var commands = new Dictionary<string, (string desc, Func<string[], Task> run)>(StringComparer.OrdinalIgnoreCase)
{
    ["reseed"] = ("Clear strikes table and reseed via SQS (skips last seed date)", Reseed)
};

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage: dotnet run -- [--profile <profile>] [--region <region>] <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    foreach (var (name, (desc, _)) in commands)
        Console.WriteLine($"  {name,-20} {desc}");
    return;
}

if (!commands.TryGetValue(args[0], out var cmd))
{
    Console.WriteLine($"Unknown command: {args[0]}");
    return;
}

await cmd.run(args[1..]);

// ── reseed ───────────────────────────────────────────────────────────────────
async Task Reseed(string[] opts)
{
    const string tableName   = "strikes";
    const string queueName   = "iran-conflict-map-processor";

    var seedDir = opts.Length > 0 ? opts[0]
        : FindSeedDir() ?? throw new Exception("Could not locate seed/criticial-threats. Pass path as first argument.");

    var files = Directory.GetFiles(seedDir, "*.json").OrderBy(f => f).ToList();
    if (files.Count == 0) throw new Exception($"No seed files found in {seedDir}");

    // Skip last date — will be written by sync pipeline
    var skipped = Path.GetFileName(files[^1]);
    files = files[..^1];
    Console.WriteLine($"Skipping last seed file: {skipped} (will be written by sync)");
    Console.WriteLine($"Seed files to load: {files.Count}");
    foreach (var f in files) Console.WriteLine($"  {Path.GetFileName(f)}");
    Console.WriteLine();

    var dynamo = BuildDynamoClient();
    var sqs    = BuildSqsClient();

    // ── 1. Get queue URL ─────────────────────────────────────────────────────
    var queueUrl = (await sqs.GetQueueUrlAsync(queueName)).QueueUrl;
    Console.WriteLine($"Queue: {queueUrl}");
    Console.WriteLine();

    // ── 2. Clear the table ───────────────────────────────────────────────────
    Console.Write("Scanning table for existing items... ");
    var allKeys = new List<Dictionary<string, AttributeValue>>();
    Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
    do
    {
        var scan = await dynamo.ScanAsync(new ScanRequest
        {
            TableName            = tableName,
            ProjectionExpression = "id",
            ExclusiveStartKey    = lastEvaluatedKey
        });
        allKeys.AddRange(scan.Items.Select(i => new Dictionary<string, AttributeValue> { ["id"] = i["id"] }));
        lastEvaluatedKey = scan.LastEvaluatedKey?.Count > 0 ? scan.LastEvaluatedKey : null;
    } while (lastEvaluatedKey != null);

    Console.WriteLine($"{allKeys.Count} items found.");

    if (allKeys.Count > 0)
    {
        Console.Write("Deleting... ");
        await BatchDelete(dynamo, tableName, allKeys);
        Console.WriteLine("done.");
    }

    // ── 3. Send seed files to SQS ────────────────────────────────────────────
    Console.WriteLine();
    var totalMessages = 0;

    foreach (var file in files)
    {
        var json = await File.ReadAllTextAsync(file);
        var doc  = JsonDocument.Parse(json);

        // Wrap in SyncEnvelope and forward verbatim — Processor Lambda handles new/updates/ambiguous
        doc.RootElement.TryGetProperty("new",       out var newArr);
        doc.RootElement.TryGetProperty("updates",   out var updatesArr);
        doc.RootElement.TryGetProperty("ambiguous", out var ambiguousArr);

        var hasContent = (newArr.ValueKind       == JsonValueKind.Array && newArr.GetArrayLength()       > 0)
                      || (updatesArr.ValueKind   == JsonValueKind.Array && updatesArr.GetArrayLength()   > 0)
                      || (ambiguousArr.ValueKind == JsonValueKind.Array && ambiguousArr.GetArrayLength() > 0);

        if (!hasContent)
        {
            Console.WriteLine($"  {Path.GetFileName(file)}: no events/updates/ambiguous, skipping");
            continue;
        }

        var envelope = JsonSerializer.Serialize(new
        {
            source_url = (string?)null,
            synced_at  = DateTime.UtcNow.ToString("o"),
            @new       = newArr.ValueKind       == JsonValueKind.Array ? newArr       : (JsonElement?)null,
            updates    = updatesArr.ValueKind   == JsonValueKind.Array ? updatesArr   : (JsonElement?)null,
            ambiguous  = ambiguousArr.ValueKind == JsonValueKind.Array ? ambiguousArr : (JsonElement?)null
        });

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl       = queueUrl,
            MessageBody    = envelope,
            MessageGroupId = "sync"
        });

        var newCount  = newArr.ValueKind       == JsonValueKind.Array ? newArr.GetArrayLength()       : 0;
        var updCount  = updatesArr.ValueKind   == JsonValueKind.Array ? updatesArr.GetArrayLength()   : 0;
        var ambCount  = ambiguousArr.ValueKind == JsonValueKind.Array ? ambiguousArr.GetArrayLength() : 0;
        Console.WriteLine($"  {Path.GetFileName(file)}: {newCount} new, {updCount} updates, {ambCount} ambiguous — queued");
        totalMessages++;
    }

    Console.WriteLine();
    Console.WriteLine($"Reseed complete. {totalMessages} messages sent to processor queue.");
    Console.WriteLine("Monitor the processor Lambda logs to confirm writes.");
}

// ── AWS Client Builders ───────────────────────────────────────────────────────

IAmazonDynamoDB BuildDynamoClient()
{
    var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    var chain = new CredentialProfileStoreChain();
    if (chain.TryGetAWSCredentials(profile, out var creds))
    {
        Console.WriteLine($"Using profile '{profile}' in {region}");
        return new AmazonDynamoDBClient(creds, endpoint);
    }
    Console.WriteLine($"Profile '{profile}' not found — using default credential chain in {region}");
    return new AmazonDynamoDBClient(new AmazonDynamoDBConfig { RegionEndpoint = endpoint });
}

IAmazonSQS BuildSqsClient()
{
    var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    var chain = new CredentialProfileStoreChain();
    if (chain.TryGetAWSCredentials(profile, out var creds))
        return new AmazonSQSClient(creds, endpoint);
    return new AmazonSQSClient(new AmazonSQSConfig { RegionEndpoint = endpoint });
}

// ── Helpers ───────────────────────────────────────────────────────────────────

async Task BatchDelete(IAmazonDynamoDB dynamo, string table, List<Dictionary<string, AttributeValue>> keys)
{
    var requests = keys.Select(k => new WriteRequest { DeleteRequest = new DeleteRequest { Key = k } }).ToList();
    for (var i = 0; i < requests.Count; i += 25)
    {
        var remaining = new Dictionary<string, List<WriteRequest>>
        {
            [table] = requests.Skip(i).Take(25).ToList()
        };
        var attempts = 0;
        while (remaining.Count > 0 && attempts++ < 5)
        {
            var resp = await dynamo.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = remaining });
            remaining = resp.UnprocessedItems;
            if (remaining.Count > 0) await Task.Delay(200 * attempts);
        }
    }
}

string? GetFlag(ref string[] a, string flag)
{
    var idx = Array.IndexOf(a, flag);
    if (idx == -1 || idx + 1 >= a.Length) return null;
    var val = a[idx + 1];
    a = a.Where((_, i) => i != idx && i != idx + 1).ToArray();
    return val;
}

string? FindSeedDir()
{
    var dir = Directory.GetCurrentDirectory();
    for (var i = 0; i < 6; i++)
    {
        var candidate = Path.Combine(dir, "seed", "criticial-threats");
        if (Directory.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent == null) break;
        dir = parent;
    }
    return null;
}
