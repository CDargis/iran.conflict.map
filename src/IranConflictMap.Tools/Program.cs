using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SQS;
using Amazon.SQS.Model;
using MimeKit;
using System.Text.Json;
using System.Text.RegularExpressions;

// ── Global flags ─────────────────────────────────────────────────────────────
var profile  = GetFlag(ref args, "--profile") ?? "default";
var region   = GetFlag(ref args, "--region")  ?? "us-east-1";

var commands = new Dictionary<string, (string desc, Func<string[], Task> run)>(StringComparer.OrdinalIgnoreCase)
{
    ["reseed"]        = ("Clear strikes table and reseed via SQS (skips last seed date)", Reseed),
    ["submit-url"]    = ("Submit a report URL directly to the report queue for processing", SubmitUrl),
    ["test-hubspot"]  = ("Extract HubSpot link from a raw email file and resolve the redirect body", TestHubspot),
    ["test-ctp-feed"] = ("Check criticalthreats.org for RSS feed and Iran update listing", TestCtpFeed),
    ["test-ctp-api"]  = ("Probe the criticalthreats.org API for Iran update articles", TestCtpApi),
    ["test-ctp-url"]  = ("Test URL construction from email subject for a given raw email file", TestCtpUrl),
    ["test-ctp-fetch"] = ("Fetch a criticalthreats.org article URL and show what text content is available", TestCtpFetch),
    ["test-ini-list"]  = ("Dump the INI_LIST slugs from the CTP Iran updates listing page", TestIniList),
    ["dlq-to-review"]      = ("Migrate DLQ messages matching a timestamp (--timestamp 'yyyy-MM-dd HH:mm:ss', ±60s window) to review queue", DlqToReview),
    ["normalize-review"]   = ("Convert legacy bare UpdateEvent messages in review queue to wrapped format [--confirm]", NormalizeReview),
    ["drain-review"]       = ("Save all review queue messages to review-backlog/ and delete from queue [--confirm]", DrainReview),
    ["drain-dlq"]          = ("Save all DLQ messages to dlq-backlog/ and delete from queue [--confirm]", DrainDlq)
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

// ── submit-url ────────────────────────────────────────────────────────────────
async Task SubmitUrl(string[] opts)
{
    var url = opts.Length > 0 ? opts[0]
        : throw new Exception("Usage: submit-url <report-url>");

    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        throw new Exception("URL must start with https://");

    const string queueName = "iran-conflict-map-report.fifo";

    var sqs      = BuildSqsClient();
    var queueUrl = (await sqs.GetQueueUrlAsync(queueName)).QueueUrl;

    var body = System.Text.Json.JsonSerializer.Serialize(new { url });

    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl       = queueUrl,
        MessageBody    = body,
        MessageGroupId = "manual"
    });

    Console.WriteLine($"Queued: {url}");
    Console.WriteLine("Monitor the sync Lambda logs to track progress.");
}

// ── reseed ────────────────────────────────────────────────────────────────────
async Task Reseed(string[] opts)
{
    const string tableName   = "strikes";
    const string queueName   = "iran-conflict-map-processor.fifo";

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

// ── test-hubspot ─────────────────────────────────────────────────────────────
async Task TestHubspot(string[] opts)
{
    var emailPath = opts.Length > 0 ? opts[0]
        : throw new Exception("Usage: test-hubspot <path-to-raw-email>");

    Console.WriteLine($"Reading email: {emailPath}");
    using var stream = File.OpenRead(emailPath);
    var message = await MimeMessage.LoadAsync(stream);
    Console.WriteLine($"  Subject : {message.Subject}");
    Console.WriteLine($"  From    : {message.From}");

    var htmlBody = message.HtmlBody ?? "";
    if (string.IsNullOrEmpty(htmlBody))
    {
        Console.WriteLine("No HTML body found in email.");
        return;
    }
    Console.WriteLine($"  HTML body length: {htmlBody.Length} chars");

    // ── Step 1: extract HubSpot link (same logic as sync Lambda) ─────────────
    var anchorRe = new Regex(@"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    string? hubspotUrl = null;

    foreach (Match m in anchorRe.Matches(htmlBody))
    {
        var text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
        if (text.Contains("Iran Update", StringComparison.OrdinalIgnoreCase))
        {
            hubspotUrl = m.Groups[1].Value;
            Console.WriteLine($"\n[primary match] text: '{text[..Math.Min(text.Length, 80)]}'");
            Console.WriteLine($"  href: {hubspotUrl}");
            break;
        }
    }

    if (hubspotUrl == null)
    {
        Console.WriteLine("\nPrimary match failed — trying fallback keywords...");
        foreach (Match m in anchorRe.Matches(htmlBody))
        {
            var href = m.Groups[1].Value;
            var text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
            if (href.Contains("hubspotlinks", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("criticalthreats", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("read the", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("view update", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("full update", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("full report", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("see full", StringComparison.OrdinalIgnoreCase)))
            {
                hubspotUrl = href;
                Console.WriteLine($"\n[fallback match] text: '{text[..Math.Min(text.Length, 80)]}'");
                Console.WriteLine($"  href: {hubspotUrl}");
                break;
            }
        }
    }

    if (hubspotUrl == null)
    {
        Console.WriteLine("\nNo suitable HubSpot link found. Dumping all hrefs:");
        foreach (Match m in anchorRe.Matches(htmlBody))
        {
            var text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
            Console.WriteLine($"  [{text[..Math.Min(text.Length, 50)]}] → {m.Groups[1].Value[..Math.Min(m.Groups[1].Value.Length, 80)]}");
        }
        return;
    }

    // ── Step 2: fetch the HubSpot URL and read the response body ─────────────
    Console.WriteLine("\nFetching HubSpot URL...");
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

    using var resp = await http.GetAsync(hubspotUrl);
    var finalUrl = resp.RequestMessage?.RequestUri?.ToString();
    Console.WriteLine($"  Status  : {(int)resp.StatusCode} {resp.StatusCode}");
    Console.WriteLine($"  Final URL: {finalUrl}");

    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"  Body length: {body.Length} chars");

    // ── Step 3: look for criticalthreats.org URL in the response body ─────────
    var ctpRe = new Regex(@"https?://[^\s""'<>]*criticalthreats\.org/analysis/[^\s""'<>]*",
        RegexOptions.IgnoreCase);
    var ctpMatches = ctpRe.Matches(body).Select(m => m.Value).Distinct().ToList();

    if (ctpMatches.Count > 0)
    {
        Console.WriteLine($"\n criticalthreats.org URL(s) found in response body:");
        foreach (var u in ctpMatches)
            Console.WriteLine($"  {u}");
    }
    else
    {
        Console.WriteLine("\nNo criticalthreats.org/analysis URL found in response body.");
        Console.WriteLine("Dumping first 2000 chars of body to inspect:");
        Console.WriteLine(body[..Math.Min(body.Length, 2000)]);
    }
}

// ── test-ctp-feed ─────────────────────────────────────────────────────────────
async Task TestCtpFeed(string[] opts)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

    var candidates = new[]
    {
        "https://www.criticalthreats.org/feed",
        "https://www.criticalthreats.org/feed.xml",
        "https://www.criticalthreats.org/rss",
        "https://www.criticalthreats.org/rss.xml",
        "https://www.criticalthreats.org/analysis/feed",
        "https://www.criticalthreats.org/category/iran/feed",
    };

    Console.WriteLine("=== Checking for RSS feed ===");
    foreach (var url in candidates)
    {
        try
        {
            var resp = await http.GetAsync(url);
            Console.WriteLine($"  {(int)resp.StatusCode}  {url}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"       Content-Type: {resp.Content.Headers.ContentType}");
                Console.WriteLine($"       Body preview: {body[..Math.Min(body.Length, 300)]}");
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERR  {url}  ({ex.Message})");
        }
    }

    Console.WriteLine("\n=== Checking Iran update listing page ===");
    var listingUrls = new[]
    {
        "https://www.criticalthreats.org/analysis/iran-update",
        "https://www.criticalthreats.org/briefs/iran-update",
        "https://www.criticalthreats.org/tag/iran-update",
        "https://www.criticalthreats.org/category/iran-update",
    };

    var iranRe = new Regex(
        @"https?://(?:www\.)?criticalthreats\.org/analysis/iran[^\s""'<>]*",
        RegexOptions.IgnoreCase);

    foreach (var url in listingUrls)
    {
        try
        {
            var resp = await http.GetAsync(url);
            Console.WriteLine($"  {(int)resp.StatusCode}  {url}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();

                // Look for all hrefs containing "iran" to see article link patterns
                var hrefRe = new Regex(@"href=""([^""]*iran[^""]*)""", RegexOptions.IgnoreCase);
                var hrefs  = hrefRe.Matches(body).Select(m => m.Groups[1].Value).Distinct().Take(20).ToList();

                if (hrefs.Count > 0)
                {
                    Console.WriteLine($"       hrefs containing 'iran' ({hrefs.Count} found):");
                    foreach (var h in hrefs) Console.WriteLine($"         {h}");
                }
                else
                {
                    Console.WriteLine($"       No iran hrefs found — dumping 3000 chars:");
                    Console.WriteLine(body[..Math.Min(body.Length, 3000)]);
                }
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERR  {url}  ({ex.Message})");
        }
    }
}

// ── test-ctp-api ──────────────────────────────────────────────────────────────
async Task TestCtpApi(string[] opts)
{
    const string apiBase = "https://api.criticalthreats.org/v1";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    http.DefaultRequestHeaders.Add("Accept", "application/json");

    var candidates = new[]
    {
        $"{apiBase}/analyses?search=iran+update&limit=5",
        $"{apiBase}/analyses?category=iran-update&limit=5",
        $"{apiBase}/analyses?tag=iran-update&limit=5",
        $"{apiBase}/publications?search=iran+update&limit=5",
        $"{apiBase}/posts?search=iran+update&limit=5",
        $"{apiBase}/content?search=iran+update&limit=5",
        $"{apiBase}/articles?search=iran+update&limit=5",
        $"{apiBase}/analyses?limit=5",
    };

    foreach (var url in candidates)
    {
        try
        {
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"{(int)resp.StatusCode}  {url}");
            if (resp.IsSuccessStatusCode)
            {
                Console.WriteLine(body[..Math.Min(body.Length, 800)]);
                Console.WriteLine();
                break; // stop at first working endpoint
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERR  {url}  ({ex.Message})");
        }
    }
}

// ── test-ctp-url ──────────────────────────────────────────────────────────────
async Task TestCtpUrl(string[] opts)
{
    var emailPath = opts.Length > 0 ? opts[0]
        : throw new Exception("Usage: test-ctp-url <path-to-raw-email>");

    using var stream = File.OpenRead(emailPath);
    var message = await MimeMessage.LoadAsync(stream);
    var subject = message.Subject ?? "";
    Console.WriteLine($"Subject: {subject}");

    // Extract date from subject — look for "Month DD, YYYY"
    var dateRe = new Regex(@"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2}),\s+(\d{4})\b",
        RegexOptions.IgnoreCase);
    var dm = dateRe.Match(subject);
    if (!dm.Success)
    {
        Console.WriteLine("Could not extract date from subject.");
        return;
    }

    var month = dm.Groups[1].Value.ToLowerInvariant();
    var day   = int.Parse(dm.Groups[2].Value);
    var year  = dm.Groups[3].Value;
    Console.WriteLine($"Parsed date: {month} {day}, {year}");

    // Build candidate URLs — standard pattern first, then variations
    var candidates = new[]
    {
        $"https://www.criticalthreats.org/analysis/iran-update-{month}-{day}-{year}",
        $"https://www.criticalthreats.org/analysis/iran-war-update-{month}-{day}-{year}",
        $"https://www.criticalthreats.org/analysis/iran-update-evening-special-report-{month}-{day}-{year}",
        $"https://www.criticalthreats.org/analysis/iran-update-special-report-{month}-{day}-{year}",
    };

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

    foreach (var url in candidates)
    {
        var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        var title = Regex.Match(body, @"<title>([^<]+)</title>").Groups[1].Value.Trim();
        Console.WriteLine($"\n{(int)resp.StatusCode}  {url}");
        Console.WriteLine($"  Title: {title}");
        if (resp.IsSuccessStatusCode && !title.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  *** VALID ARTICLE ***");
            // Show a snippet of visible text
            var text = Regex.Replace(body, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();
            Console.WriteLine($"  Preview: {text[..Math.Min(text.Length, 300)]}");
        }
    }
}

// ── test-ctp-fetch ────────────────────────────────────────────────────────────
async Task TestCtpFetch(string[] opts)
{
    var url = opts.Length > 0 ? opts[0]
        : throw new Exception("Usage: test-ctp-fetch <url>");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

    var html = await http.GetStringAsync(url);
    Console.WriteLine($"Fetched {html.Length} chars");

    // Strip tags — what would FetchReportPage give Claude?
    var text = Regex.Replace(html, @"<[^>]+>", " ");
    text = Regex.Replace(text, @"[ \t]{2,}", " ");
    text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n").Trim();
    Console.WriteLine($"Stripped text length: {text.Length} chars");
    Console.WriteLine("\n--- First 1000 chars of stripped text ---");
    Console.WriteLine(text[..Math.Min(text.Length, 1000)]);

    // Check for JSON-LD or embedded data that might contain article content
    Console.WriteLine("\n--- Checking for embedded JSON/data in <script> tags ---");
    var scriptRe = new Regex(@"<script[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase);
    foreach (Match m in scriptRe.Matches(html))
    {
        var content = m.Groups[1].Value.Trim();
        // Look for script blocks that contain article-like content (long text, not just JS)
        if (content.Length > 200 && (
            content.Contains("iran", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("\"body\"", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("\"content\"", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("@type", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"\nScript block ({content.Length} chars):");
            Console.WriteLine(content[..Math.Min(content.Length, 600)]);
        }
    }
}

// ── test-ini-list ─────────────────────────────────────────────────────────────
async Task TestIniList(string[] opts)
{
    const string listingUrl = "https://www.criticalthreats.org/analysis/ctp-iran-updates";
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

    var html = await http.GetStringAsync(listingUrl);

    var iniMatch = Regex.Match(html, @"var INI_LIST\s*=\s*(\[.*?\]);", RegexOptions.Singleline);
    if (!iniMatch.Success) { Console.WriteLine("INI_LIST not found"); return; }

    var entries = Regex.Matches(iniMatch.Groups[1].Value,
        @"""slug""\s*:\s*""([^""]+)""[^}]*""title""\s*:\s*""([^""]+)""");

    // also try title-first order
    if (entries.Count == 0)
        entries = Regex.Matches(iniMatch.Groups[1].Value,
            @"""title""\s*:\s*""([^""]+)""[^}]*""slug""\s*:\s*""([^""]+)""");

    Console.WriteLine($"INI_LIST total JSON length: {iniMatch.Groups[1].Value.Length} chars");

    // Extract all slug+title pairs
    var slugRe = new Regex(@"\{[^}]*""slug""\s*:\s*""([^""]+)""[^}]*""title""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
    var allMatches = slugRe.Matches(iniMatch.Groups[1].Value).ToList();
    Console.WriteLine($"Found {allMatches.Count} entries. Most recent 10:");
    foreach (var m in allMatches.Take(10))
        Console.WriteLine($"  {m.Groups[1].Value}");

    Console.WriteLine("\nSearching for 'march-15':");
    foreach (var m in allMatches.Where(m => m.Groups[1].Value.Contains("march-15")))
        Console.WriteLine($"  {m.Groups[1].Value}");
}

// ── dlq-to-review ─────────────────────────────────────────────────────────────
async Task DlqToReview(string[] opts)
{
    var confirm      = opts.Contains("--confirm");
    var tsFlag       = GetFlag(ref opts, "--timestamp");
    var windowFlag   = GetFlag(ref opts, "--window");
    var windowSecs   = windowFlag != null ? int.Parse(windowFlag) : 60;

    if (tsFlag == null)
        throw new Exception("Usage: dlq-to-review --timestamp 'yyyy-MM-dd HH:mm:ss' [--window <seconds>] [--confirm]");

    var anchor = DateTime.Parse(tsFlag, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();

    const string dlqName    = "iran-conflict-map-dlq.fifo";
    const string reviewName = "iran-conflict-map-review.fifo";

    var sqs       = BuildSqsClient();
    var dlqUrl    = (await sqs.GetQueueUrlAsync(dlqName)).QueueUrl;
    var reviewUrl = (await sqs.GetQueueUrlAsync(reviewName)).QueueUrl;

    Console.WriteLine($"DLQ       : {dlqUrl}");
    Console.WriteLine($"Review    : {reviewUrl}");
    Console.WriteLine($"Timestamp : {anchor:yyyy-MM-dd HH:mm:ss} UTC ±{windowSecs}s");
    Console.WriteLine($"Mode      : {(confirm ? "LIVE — will migrate and delete" : "DRY RUN — pass --confirm to migrate")}");
    Console.WriteLine();

    var all      = new List<Message>();
    var received = new HashSet<string>();

    // Drain the full queue using long polling (WaitTimeSeconds=20).
    // Retry up to 3 consecutive empty responses before giving up, in case
    // messages are temporarily invisible from a prior receive.
    var emptyRetries = 0;
    while (emptyRetries < 3)
    {
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl                    = dlqUrl,
            MaxNumberOfMessages         = 10,
            VisibilityTimeout           = 30,
            WaitTimeSeconds             = 20,
            MessageSystemAttributeNames = new List<string> { "All" }
        });

        if (resp.Messages.Count == 0)
        {
            emptyRetries++;
            continue;
        }

        int newCount = 0;
        foreach (var msg in resp.Messages)
        {
            if (received.Add(msg.MessageId)) { all.Add(msg); newCount++; }
        }
        if (newCount == 0) emptyRetries++;
        else emptyRetries = 0;
    }

    Console.WriteLine($"Found {all.Count} message(s) in DLQ total.");

    var selected = new List<Message>();
    var skipped  = new List<Message>();

    foreach (var msg in all)
    {
        var sentUtc = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(msg.Attributes["SentTimestamp"])).UtcDateTime;
        var diff    = Math.Abs((sentUtc - anchor).TotalSeconds);
        if (diff <= windowSecs)
        {
            selected.Add(msg);
            Console.WriteLine($"  [match]  {msg.MessageId}  sent={sentUtc:yyyy-MM-dd HH:mm:ss}  diff={diff:F0}s  body={msg.Body[..Math.Min(msg.Body.Length, 80)]}...");
        }
        else
        {
            skipped.Add(msg);
            Console.WriteLine($"  [skip]   {msg.MessageId}  sent={sentUtc:yyyy-MM-dd HH:mm:ss}  diff={diff:F0}s");
        }
    }

    // Release skipped messages immediately
    foreach (var msg in skipped)
    {
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl          = dlqUrl,
            ReceiptHandle     = msg.ReceiptHandle,
            VisibilityTimeout = 0
        });
    }

    Console.WriteLine();
    Console.WriteLine($"Matched {selected.Count} message(s), skipped {skipped.Count}.");

    if (!confirm)
    {
        Console.WriteLine("Re-run with --confirm to migrate.");
        foreach (var msg in selected)
        {
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl          = dlqUrl,
                ReceiptHandle     = msg.ReceiptHandle,
                VisibilityTimeout = 0
            });
        }
        return;
    }

    Console.WriteLine($"Migrating {selected.Count} message(s) to review queue...");
    var totalItems = 0;
    foreach (var msg in selected)
    {
        var sent = await ExpandToReviewQueue(sqs, reviewUrl, msg.Body);
        totalItems += sent;

        await sqs.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl      = dlqUrl,
            ReceiptHandle = msg.ReceiptHandle
        });

        Console.WriteLine($"  migrated {msg.MessageId} → {sent} review item(s)");
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {selected.Count} message(s) expanded into {totalItems} review item(s).");
}

// ── dlq-to-review helpers ─────────────────────────────────────────────────────

async Task<int> ExpandToReviewQueue(IAmazonSQS sqs, string reviewUrl, string body)
{
    JsonElement root;
    try { root = JsonDocument.Parse(body).RootElement; }
    catch { return 0; }

    var sourceUrl = root.TryGetProperty("source_url", out var su) ? su.GetString() : null;

    // If already a per-item review message (has "item" property), forward as-is
    if (root.TryGetProperty("item", out _))
    {
        await SendReviewMessage(sqs, reviewUrl, body);
        return 1;
    }

    // SyncEnvelope — expand into individual review items
    int sent = 0;

    if (root.TryGetProperty("new", out JsonElement newArr) && newArr.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement evt in newArr.EnumerateArray())
        {
            string itemBody = JsonSerializer.Serialize(new
            {
                source_url = sourceUrl,
                item = new { note = "Migrated from DLQ — new event", as_new = evt, as_update = (JsonElement?)null }
            });
            await SendReviewMessage(sqs, reviewUrl, itemBody);
            sent++;
        }
    }

    if (root.TryGetProperty("updates", out JsonElement updArr) && updArr.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement upd in updArr.EnumerateArray())
        {
            string itemBody = JsonSerializer.Serialize(new
            {
                source_url = sourceUrl,
                item = new { note = "Migrated from DLQ — update", as_new = (JsonElement?)null, as_update = upd }
            });
            await SendReviewMessage(sqs, reviewUrl, itemBody);
            sent++;
        }
    }

    if (root.TryGetProperty("ambiguous", out JsonElement ambArr) && ambArr.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement amb in ambArr.EnumerateArray())
        {
            string ambNote = amb.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? "Migrated from DLQ — ambiguous" : "Migrated from DLQ — ambiguous";
            JsonElement? asNew    = amb.TryGetProperty("as_new",    out JsonElement an) ? an : null;
            JsonElement? asUpdate = amb.TryGetProperty("as_update", out JsonElement au) ? au : null;
            string itemBody = JsonSerializer.Serialize(new
            {
                source_url = sourceUrl,
                item = new { note = ambNote, as_new = asNew, as_update = asUpdate }
            });
            await SendReviewMessage(sqs, reviewUrl, itemBody);
            sent++;
        }
    }

    return sent;
}

async Task SendReviewMessage(IAmazonSQS sqs, string reviewUrl, string body)
{
    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl       = reviewUrl,
        MessageBody    = body,
        MessageGroupId = "review"
    });
}

// ── normalize-review ──────────────────────────────────────────────────────────

async Task NormalizeReview(string[] opts)
{
    var confirm     = opts.Contains("--confirm");
    const string reviewName = "iran-conflict-map-review.fifo";

    var sqs       = BuildSqsClient();
    var reviewUrl = (await sqs.GetQueueUrlAsync(reviewName)).QueueUrl;

    Console.WriteLine($"Review queue : {reviewUrl}");
    Console.WriteLine($"Mode         : {(confirm ? "LIVE — will convert and re-queue" : "DRY RUN — pass --confirm to apply")}");
    Console.WriteLine();

    var all      = new List<Message>();
    var received = new HashSet<string>();
    var emptyRetries = 0;

    while (emptyRetries < 3)
    {
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl            = reviewUrl,
            MaxNumberOfMessages = 10,
            VisibilityTimeout   = 300,
            WaitTimeSeconds     = 20
        });

        if (resp.Messages.Count == 0) { emptyRetries++; continue; }

        int newCount = 0;
        foreach (var msg in resp.Messages)
        {
            if (received.Add(msg.MessageId)) { all.Add(msg); newCount++; }
        }
        if (newCount == 0) emptyRetries++;
        else emptyRetries = 0;
    }

    Console.WriteLine($"Found {all.Count} message(s) in review queue.");

    var legacy  = new List<Message>();
    var current = new List<Message>();

    foreach (var msg in all)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(msg.Body).RootElement; }
        catch { Console.WriteLine($"  [skip] {msg.MessageId} — unparseable JSON"); continue; }

        // Legacy: bare UpdateEvent — has "lookup" at root, no "item" wrapper
        bool isLegacy = root.TryGetProperty("lookup", out _) && !root.TryGetProperty("item", out _);

        if (isLegacy)
        {
            legacy.Add(msg);
            Console.WriteLine($"  [legacy]  {msg.MessageId}  body={msg.Body[..Math.Min(msg.Body.Length, 80)]}...");
        }
        else
        {
            current.Add(msg);
            Console.WriteLine($"  [current] {msg.MessageId}");
        }
    }

    // Release all messages — ignore stale handle errors (timeout already expired, SQS will re-enqueue)
    foreach (var msg in current.Concat(confirm ? Enumerable.Empty<Message>() : legacy))
    {
        try
        {
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl          = reviewUrl,
                ReceiptHandle     = msg.ReceiptHandle,
                VisibilityTimeout = 0
            });
        }
        catch (Amazon.SQS.AmazonSQSException) { /* handle expired — message will re-appear automatically */ }
    }

    Console.WriteLine();
    Console.WriteLine($"Legacy: {legacy.Count}  Current: {current.Count}");

    if (!confirm)
    {
        Console.WriteLine("Re-run with --confirm to convert.");
        return;
    }

    Console.WriteLine($"Converting {legacy.Count} legacy message(s)...");
    int converted = 0;

    foreach (var msg in legacy)
    {
        JsonElement root  = JsonDocument.Parse(msg.Body).RootElement;
        object?     asNew = BuildAsNewFromLegacyUpdate(root);

        string newBody = JsonSerializer.Serialize(new
        {
            source_url = (string?)null,
            item       = new
            {
                note      = "Legacy queued update (normalized)",
                as_new    = asNew,
                as_update = (object?)root
            }
        });

        await SendReviewMessage(sqs, reviewUrl, newBody);
        await sqs.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl      = reviewUrl,
            ReceiptHandle = msg.ReceiptHandle
        });

        converted++;
        Console.WriteLine($"  converted {msg.MessageId}");
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {converted} message(s) converted.");
}

// ── drain-review ──────────────────────────────────────────────────────────────

async Task DrainReview(string[] opts)
{
    var outDir = GetFlag(ref opts, "--out") ?? FindRepoRoot("review-backlog");
    await DrainQueue(opts, "iran-conflict-map-review.fifo", outDir);
}

// ── drain-dlq ─────────────────────────────────────────────────────────────────

async Task DrainDlq(string[] opts)
{
    var outDir = GetFlag(ref opts, "--out") ?? FindRepoRoot("dlq-backlog");
    await DrainQueue(opts, "iran-conflict-map-dlq.fifo", outDir);
}

// ── DrainQueue (shared) ───────────────────────────────────────────────────────

async Task DrainQueue(string[] opts, string queueName, string outDir)
{
    var confirm = opts.Contains("--confirm");

    var sqs      = BuildSqsClient();
    var queueUrl = (await sqs.GetQueueUrlAsync(queueName)).QueueUrl;

    Console.WriteLine($"Queue      : {queueUrl}");
    Console.WriteLine($"Output dir : {outDir}");
    Console.WriteLine($"Mode       : {(confirm ? "LIVE — will save and delete" : "DRY RUN — pass --confirm to apply")}");
    Console.WriteLine();

    var all          = new List<Message>();
    var received     = new HashSet<string>();
    var emptyRetries = 0;

    while (emptyRetries < 3)
    {
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl                    = queueUrl,
            MaxNumberOfMessages         = 10,
            VisibilityTimeout           = 300,
            WaitTimeSeconds             = 20,
            MessageSystemAttributeNames = new List<string> { "All" }
        });

        if (resp.Messages.Count == 0) { emptyRetries++; continue; }

        int newCount = 0;
        foreach (var msg in resp.Messages)
        {
            if (received.Add(msg.MessageId)) { all.Add(msg); newCount++; }
        }
        if (newCount == 0) emptyRetries++;
        else emptyRetries = 0;
    }

    Console.WriteLine($"Found {all.Count} message(s).");
    if (all.Count == 0) return;

    if (confirm)
    {
        Directory.CreateDirectory(outDir);
    }

    foreach (var msg in all)
    {
        var sentMs   = msg.Attributes.TryGetValue("SentTimestamp", out string? ts)
            ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(ts)).UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ss")
            : "unknown";
        var fileName = $"{sentMs}_{msg.MessageId[..8]}.json";
        var filePath = Path.Combine(outDir, fileName);

        string content;
        try
        {
            var doc = JsonDocument.Parse(msg.Body);
            content = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { content = msg.Body; }

        Console.WriteLine($"  {fileName}");

        if (confirm)
        {
            await File.WriteAllTextAsync(filePath, content);
            await sqs.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl      = queueUrl,
                ReceiptHandle = msg.ReceiptHandle
            });
        }
    }

    if (!confirm)
    {
        foreach (var msg in all)
        {
            try
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl          = queueUrl,
                    ReceiptHandle     = msg.ReceiptHandle,
                    VisibilityTimeout = 0
                });
            }
            catch (AmazonSQSException) { }
        }
        Console.WriteLine();
        Console.WriteLine("Re-run with --confirm to save and delete.");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine($"Done. {all.Count} message(s) saved to {outDir} and deleted from queue.");
    }
}

// Build a best-effort as_new PutRequest from a legacy bare UpdateEvent.
// Uses lookup (date/lat/lng) + changes (already DynamoDB wire format) + entity="strike".
// The processor overwrites id on write, so we omit it here.
object? BuildAsNewFromLegacyUpdate(JsonElement root)
{
    if (!root.TryGetProperty("lookup",  out JsonElement lookup)  ||
        !root.TryGetProperty("changes", out JsonElement changes))
        return null;

    using var ms     = new System.IO.MemoryStream();
    using var writer = new System.Text.Json.Utf8JsonWriter(ms);

    writer.WriteStartObject();                    // { PutRequest: { Item: { ... } } }
    writer.WritePropertyName("PutRequest");
    writer.WriteStartObject();
    writer.WritePropertyName("Item");
    writer.WriteStartObject();

    writer.WritePropertyName("entity");
    writer.WriteStartObject(); writer.WriteString("S", "strike"); writer.WriteEndObject();

    if (lookup.TryGetProperty("date", out JsonElement date) && date.ValueKind == JsonValueKind.String)
    {
        writer.WritePropertyName("date");
        writer.WriteStartObject(); writer.WriteString("S", date.GetString()); writer.WriteEndObject();
    }

    if (lookup.TryGetProperty("lat", out JsonElement lat) && lat.ValueKind == JsonValueKind.Number)
    {
        writer.WritePropertyName("lat");
        writer.WriteStartObject(); writer.WriteString("N", lat.GetDouble().ToString("G")); writer.WriteEndObject();
    }

    if (lookup.TryGetProperty("lng", out JsonElement lng) && lng.ValueKind == JsonValueKind.Number)
    {
        writer.WritePropertyName("lng");
        writer.WriteStartObject(); writer.WriteString("N", lng.GetDouble().ToString("G")); writer.WriteEndObject();
    }

    foreach (JsonProperty prop in changes.EnumerateObject())
    {
        writer.WritePropertyName(prop.Name);
        prop.Value.WriteTo(writer);
    }

    writer.WriteEndObject(); // Item
    writer.WriteEndObject(); // PutRequest
    writer.WriteEndObject(); // root
    writer.Flush();

    return JsonDocument.Parse(ms.ToArray()).RootElement;
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

string FindRepoRoot(string subdir)
{
    var dir = Directory.GetCurrentDirectory();
    for (var i = 0; i < 6; i++)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
            return Path.Combine(dir, subdir);
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent == null) break;
        dir = parent;
    }
    // Fallback: relative to cwd
    return Path.Combine(Directory.GetCurrentDirectory(), subdir);
}
