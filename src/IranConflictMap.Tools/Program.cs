using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using System.Text.Json;

var commands = new Dictionary<string, (string desc, Func<string[], Task> run)>(StringComparer.OrdinalIgnoreCase)
{
    ["reseed"] = ("Clear strikes table and reseed from seed files (skips last date)", Reseed)
};

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage: dotnet run -- <command> [options]");
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
    var tableName = "strikes";
    var skipLast  = true;   // skip the most recent seed date — will be written by sync

    // Allow overriding the seed directory
    var seedDir = opts.Length > 0 ? opts[0]
        : FindSeedDir() ?? throw new Exception("Could not locate seed/criticial-threats directory. Pass path as first argument.");

    var files = Directory.GetFiles(seedDir, "*.json").OrderBy(f => f).ToList();
    if (files.Count == 0) throw new Exception($"No seed files found in {seedDir}");

    if (skipLast)
    {
        var skipped = Path.GetFileName(files[^1]);
        files = files[..^1];
        Console.WriteLine($"Skipping last seed file: {skipped} (will be written by sync)");
    }

    Console.WriteLine($"Seed files to load: {files.Count}");
    foreach (var f in files) Console.WriteLine($"  {Path.GetFileName(f)}");
    Console.WriteLine();

    var dynamo = new AmazonDynamoDBClient();

    // ── 1. Clear the table ───────────────────────────────────────────────────
    Console.Write("Scanning table for existing items... ");
    var allKeys = new List<Dictionary<string, AttributeValue>>();
    string? lastKey = null;
    do
    {
        var scan = await dynamo.ScanAsync(new ScanRequest
        {
            TableName            = tableName,
            ProjectionExpression = "id",
            ExclusiveStartKey    = lastKey == null ? null : new Dictionary<string, AttributeValue>
            {
                ["id"] = new AttributeValue { S = lastKey }
            }
        });
        allKeys.AddRange(scan.Items.Select(i => new Dictionary<string, AttributeValue> { ["id"] = i["id"] }));
        lastKey = scan.LastEvaluatedKey?.GetValueOrDefault("id")?.S;
    } while (lastKey != null);

    Console.WriteLine($"{allKeys.Count} items found.");

    if (allKeys.Count > 0)
    {
        Console.Write("Deleting... ");
        await BatchWrite(dynamo, tableName, allKeys.Select(k => new WriteRequest
        {
            DeleteRequest = new DeleteRequest { Key = k }
        }).ToList());
        Console.WriteLine("done.");
    }

    // ── 2. Load and write seed data ──────────────────────────────────────────
    var totalWritten = 0;

    foreach (var file in files)
    {
        var json = await File.ReadAllTextAsync(file);
        var doc  = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("new", out var newArr) || newArr.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine($"  {Path.GetFileName(file)}: no 'new' array, skipping");
            continue;
        }

        var puts = new List<WriteRequest>();
        foreach (var entry in newArr.EnumerateArray())
        {
            var item = entry.GetProperty("PutRequest").GetProperty("Item");
            puts.Add(new WriteRequest
            {
                PutRequest = new PutRequest { Item = ParseDynamoItem(item) }
            });
        }

        await BatchWrite(dynamo, tableName, puts);
        Console.WriteLine($"  {Path.GetFileName(file)}: {puts.Count} items written");
        totalWritten += puts.Count;
    }

    Console.WriteLine();
    Console.WriteLine($"Reseed complete. {totalWritten} items written to '{tableName}'.");
}

// ── Helpers ──────────────────────────────────────────────────────────────────

async Task BatchWrite(IAmazonDynamoDB dynamo, string table, List<WriteRequest> requests)
{
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
            if (remaining.Count > 0)
                await Task.Delay(200 * attempts);
        }

        if (remaining.Count > 0)
            Console.WriteLine($"  Warning: {remaining.Values.Sum(r => r.Count)} items still unprocessed after retries");
    }
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

Dictionary<string, AttributeValue> ParseDynamoItem(JsonElement element)
{
    var result = new Dictionary<string, AttributeValue>();
    foreach (var prop in element.EnumerateObject())
    {
        var attr = ParseDynamoAttr(prop.Value);
        if (attr != null) result[prop.Name] = attr;
    }
    return result;
}

AttributeValue? ParseDynamoAttr(JsonElement element)
{
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
        return new AttributeValue { M = ParseDynamoItem(m) };
    if (element.TryGetProperty("L", out var l))
        return new AttributeValue
        {
            L = l.EnumerateArray()
                 .Select(ParseDynamoAttr)
                 .Where(a => a != null)
                 .Select(a => a!)
                 .ToList()
        };
    if (element.TryGetProperty("SS", out var ss))
        return new AttributeValue { SS = ss.EnumerateArray().Select(x => x.GetString()!).ToList() };
    return null;
}
