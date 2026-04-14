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
    ["drain-dlq"]          = ("Save all DLQ messages to dlq-backlog/ and delete from queue [--confirm]", DrainDlq),
    ["send-envelope"]      = ("Send a SyncEnvelope JSON file to the processor queue [--file <path>] [--confirm]", SendEnvelope),
    ["dedup-strikes"]      = ("Find and remove duplicate strikes by coordinates [--date yyyy-MM-dd | --all | --run-id <id>] [--confirm]", DedupStrikes),
    ["trigger-cleanup"]    = ("Discard stale review queue items for a URL by sending a cleanup message [--url <url>] [--run-id <id>] [--confirm]", TriggerCleanup),
    ["enrich-review"]      = ("Backfill nearest_record on legacy ambiguous review queue items [--confirm]", EnrichReview),
    ["backfill-economic"]  = ("Backfill Brent price history from EIA API [--start yyyy-MM-dd] [--end yyyy-MM-dd] [--eia-key <key>] [--confirm]", BackfillEconomic),
    ["backfill-signals"]   = ("Backfill economic signals from historical CTP-ISW reports [--start yyyy-MM-dd] [--end yyyy-MM-dd] [--anthropic-key <key>] [--confirm]", BackfillSignals),
    ["stamp-signal-entity"] = ("Backfill entity='signal' on existing economic signals rows missing the attribute [--confirm]", StampSignalEntity),
    ["normalize-actors"]      = ("Normalize actor field on all strikes to the canonical actor list [--confirm]", NormalizeActors),
    ["backfill-report-date"]  = ("Backfill report_date on syncs-v2 records by parsing the report URL [--confirm]", BackfillReportDate)
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

    url = NormalizeReportUrl(url);
    var body = System.Text.Json.JsonSerializer.Serialize(new { run_id = DateTime.UtcNow.ToString("o"), url });

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

// ── send-envelope ─────────────────────────────────────────────────────────────

async Task SendEnvelope(string[] opts)
{
    var confirm  = opts.Contains("--confirm");
    var filePath = GetFlag(ref opts, "--file");

    if (string.IsNullOrEmpty(filePath))
    {
        Console.Error.WriteLine("Error: --file <path> is required.");
        return;
    }

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"Error: file not found: {filePath}");
        return;
    }

    string json = await File.ReadAllTextAsync(filePath);

    // Validate it parses and has a "new" array
    JsonDocument doc;
    try { doc = JsonDocument.Parse(json); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: invalid JSON — {ex.Message}");
        return;
    }

    int newCount = doc.RootElement.TryGetProperty("new", out JsonElement newEl) && newEl.ValueKind == JsonValueKind.Array
        ? newEl.GetArrayLength() : 0;

    var sqs          = BuildSqsClient();
    var processorUrl = (await sqs.GetQueueUrlAsync("iran-conflict-map-processor.fifo")).QueueUrl;

    Console.WriteLine($"Processor queue : {processorUrl}");
    Console.WriteLine($"File            : {filePath}");
    Console.WriteLine($"New events      : {newCount}");
    Console.WriteLine($"Mode            : {(confirm ? "LIVE — will send to processor" : "DRY RUN — pass --confirm to apply")}");

    if (!confirm) return;

    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl       = processorUrl,
        MessageBody    = json,
        MessageGroupId = "review"
    });

    Console.WriteLine();
    Console.WriteLine($"Done. Envelope sent — {newCount} new event(s) queued for processing.");
}

// ── dedup-strikes ─────────────────────────────────────────────────────────────

async Task DedupStrikes(string[] opts)
{
    var confirm   = opts.Contains("--confirm");
    var all       = opts.Contains("--all");
    var dateFlag  = GetFlag(ref opts, "--date");
    var runIdFlag = GetFlag(ref opts, "--run-id");

    if (!all && dateFlag == null && runIdFlag == null)
        throw new Exception("Usage: dedup-strikes --date yyyy-MM-dd [--confirm]\n       dedup-strikes --all [--confirm]\n       dedup-strikes --run-id <run_id> [--confirm]");

    // ── Run-id mode: delete all strikes created by a specific sync run ────────
    var dynamo = BuildDynamoClient();

    if (runIdFlag != null)
    {
        Console.WriteLine($"Table  : strikes");
        Console.WriteLine($"Run ID : {runIdFlag}");
        Console.WriteLine($"Mode   : {(confirm ? "LIVE — will delete matching strikes" : "DRY RUN — pass --confirm to delete")}");
        Console.WriteLine();

        var runItems = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? runLastKey = null;

        do
        {
            var resp = await dynamo.ScanAsync(new ScanRequest
            {
                TableName                 = "strikes",
                FilterExpression          = "created_at = :run",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":run"] = new AttributeValue { S = runIdFlag }
                },
                ExclusiveStartKey = runLastKey
            });
            runItems.AddRange(resp.Items);
            runLastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        }
        while (runLastKey != null);

        if (runItems.Count == 0)
        {
            Console.WriteLine("No strikes found for that run ID.");
            return;
        }

        foreach (Dictionary<string, AttributeValue> item in runItems)
        {
            string id    = item.TryGetValue("id",    out AttributeValue? iv) ? iv.S ?? "?" : "?";
            string date  = item.TryGetValue("date",  out AttributeValue? dv) ? dv.S ?? "?" : "?";
            string title = item.TryGetValue("title", out AttributeValue? tv) ? tv.S ?? "?" : "?";
            Console.WriteLine($"  [DEL] id={id}  date={date}  title={title}");
        }

        Console.WriteLine();
        Console.WriteLine($"Items to delete: {runItems.Count}");

        if (!confirm)
        {
            Console.WriteLine("Re-run with --confirm to delete.");
            return;
        }

        List<Dictionary<string, AttributeValue>> runKeys = runItems
            .Select(i => new Dictionary<string, AttributeValue> { ["id"] = i["id"] })
            .ToList();

        Console.Write("Deleting... ");
        await BatchDelete(dynamo, "strikes", runKeys);
        Console.WriteLine($"done. {runItems.Count} item(s) deleted.");
        return;
    }

    const string tableName = "strikes";
    const string indexName = "entity-date-index";

    Console.WriteLine($"Table  : {tableName}");
    Console.WriteLine($"Scope  : {(all ? "ALL dates" : dateFlag)}");
    Console.WriteLine($"Mode   : {(confirm ? "LIVE — will delete duplicates" : "DRY RUN — pass --confirm to delete")}");
    Console.WriteLine();

    // ── 1. Load strikes ───────────────────────────────────────────────────────
    var items = new List<Dictionary<string, AttributeValue>>();
    Dictionary<string, AttributeValue>? lastKey = null;

    if (all)
    {
        // Full scan — load all strikes across all dates
        do
        {
            var resp = await dynamo.ScanAsync(new ScanRequest
            {
                TableName         = tableName,
                FilterExpression  = "entity = :ent",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":ent"] = new AttributeValue { S = "strike" }
                },
                ExclusiveStartKey = lastKey
            });
            items.AddRange(resp.Items);
            lastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        }
        while (lastKey != null);
        Console.WriteLine($"Loaded {items.Count} strike(s) across all dates.");
    }
    else
    {
        // Query by date via GSI
        do
        {
            var resp = await dynamo.QueryAsync(new QueryRequest
            {
                TableName                 = tableName,
                IndexName                 = indexName,
                KeyConditionExpression    = "#ent = :ent AND #dt = :dt",
                ExpressionAttributeNames  = new Dictionary<string, string>
                {
                    ["#ent"] = "entity",
                    ["#dt"]  = "date"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":ent"] = new AttributeValue { S = "strike" },
                    [":dt"]  = new AttributeValue { S = dateFlag! }
                },
                ExclusiveStartKey = lastKey
            });
            items.AddRange(resp.Items);
            lastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        }
        while (lastKey != null);
        Console.WriteLine($"Found {items.Count} strike(s) on {dateFlag}.");
    }

    if (items.Count == 0) return;

    // ── 2. Group by (date, lat, lng) then cluster within each geo group ─────────
    static string CoordKey(Dictionary<string, AttributeValue> item)
    {
        string date = item.TryGetValue("date", out AttributeValue? dateVal) ? dateVal.S ?? "" : "";
        double lat  = item.TryGetValue("lat",  out AttributeValue? latVal)  && latVal.N != null
            ? Math.Round(double.Parse(latVal.N), 4) : 0;
        double lng  = item.TryGetValue("lng",  out AttributeValue? lngVal)  && lngVal.N != null
            ? Math.Round(double.Parse(lngVal.N), 4) : 0;
        return $"{date}|{lat:F4},{lng:F4}";
    }

    static string TitleKey(Dictionary<string, AttributeValue> item) =>
        item.TryGetValue("title", out AttributeValue? tv) && tv.S != null
            ? tv.S.Trim().ToLowerInvariant() : "";

    static string DescKey(Dictionary<string, AttributeValue> item) =>
        item.TryGetValue("description", out AttributeValue? dv) && dv.S != null
            ? dv.S.Trim().ToLowerInvariant()[..Math.Min(dv.S.Length, 40)] : "";

    // Within each geo group, cluster items whose title OR description prefix matches
    static List<List<Dictionary<string, AttributeValue>>> ClusterGeoGroup(
        List<Dictionary<string, AttributeValue>> geoGroup)
    {
        List<List<Dictionary<string, AttributeValue>>> clusters = new();

        foreach (Dictionary<string, AttributeValue> item in geoGroup)
        {
            string title = TitleKey(item);
            string desc  = DescKey(item);

            List<Dictionary<string, AttributeValue>>? match = clusters.FirstOrDefault(c =>
                c.Any(x =>
                    (title != "" && TitleKey(x) == title) ||
                    (desc  != "" && DescKey(x)  == desc)));

            if (match != null)
            {
                match.Add(item);
            }
            else
            {
                clusters.Add(new List<Dictionary<string, AttributeValue>> { item });
            }
        }

        return clusters;
    }

    Dictionary<string, List<Dictionary<string, AttributeValue>>> geoGroups =
        items.GroupBy(CoordKey)
             .ToDictionary(g => g.Key, g => g.ToList());

    List<Dictionary<string, AttributeValue>> duplicates = new();
    int dupGroupCount = 0;

    foreach (var (coordKey, geoGroup) in geoGroups.OrderBy(g => g.Key))
    {
        List<List<Dictionary<string, AttributeValue>>> clusters = ClusterGeoGroup(geoGroup);

        foreach (List<Dictionary<string, AttributeValue>> group in clusters)
        {
            if (group.Count == 1) continue;

            dupGroupCount++;
            string[] parts      = coordKey.Split('|');
            string   displayKey = parts.Length >= 2 ? $"{parts[0]} ({parts[1]})" : coordKey;
            Console.WriteLine($"\nDuplicate group @ {displayKey} — {group.Count} items:");

            // Keep the item with the most attributes; tie-break: earliest id
            Dictionary<string, AttributeValue> keeper = group
                .OrderByDescending(i => i.Count)
                .ThenBy(i => i.TryGetValue("id", out AttributeValue? idVal) ? idVal.S ?? "" : "")
                .First();

            foreach (Dictionary<string, AttributeValue> item in group)
            {
                bool isKeeper = item.TryGetValue("id", out AttributeValue? itemId) &&
                                keeper.TryGetValue("id", out AttributeValue? keeperId) &&
                                itemId.S == keeperId.S;

                string id          = item.TryGetValue("id",          out AttributeValue? iv) ? iv.S ?? "?" : "?";
                string description = item.TryGetValue("description", out AttributeValue? dv) ? (dv.S ?? "").Replace('\n', ' ')[..Math.Min((dv.S ?? "").Length, 60)] : "(no description)";
                int    fieldCount  = item.Count;

                Console.WriteLine($"  [{(isKeeper ? "KEEP" : "DEL ")}] id={id}  fields={fieldCount}  desc={description}");

                if (!isKeeper)
                {
                    duplicates.Add(new Dictionary<string, AttributeValue> { ["id"] = item["id"] });
                }
            }
        }
    }

    Console.WriteLine();

    if (dupGroupCount == 0)
    {
        Console.WriteLine("No duplicates found.");
        return;
    }

    Console.WriteLine($"Duplicate groups: {dupGroupCount}  Items to delete: {duplicates.Count}");

    if (!confirm)
    {
        Console.WriteLine("Re-run with --confirm to delete.");
        return;
    }

    Console.Write("Deleting... ");
    await BatchDelete(dynamo, tableName, duplicates);
    Console.WriteLine($"done. {duplicates.Count} item(s) deleted.");
}

// ── trigger-cleanup ───────────────────────────────────────────────────────────

async Task TriggerCleanup(string[] opts)
{
    var confirm    = opts.Contains("--confirm");
    var urlFlag    = GetFlag(ref opts, "--url");
    var runIdFlag  = GetFlag(ref opts, "--run-id");

    if (urlFlag == null)
        throw new Exception("Usage: trigger-cleanup --url <report-url> [--run-id <run_id>] [--confirm]");

    string normalizedUrl = NormalizeReportUrl(urlFlag);

    string runId;

    if (runIdFlag != null)
    {
        runId = runIdFlag;
        Console.WriteLine($"Using provided run_id: {runId}");
    }
    else
    {
        // Look up the most recent run_id for this URL from syncs-v2
        var dynamo = BuildDynamoClient();

        var resp = await dynamo.QueryAsync(new QueryRequest
        {
            TableName                 = "syncs-v2",
            KeyConditionExpression    = "report_url = :url",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":url"] = new AttributeValue { S = normalizedUrl }
            },
            ScanIndexForward          = false,  // descending by run_id — most recent first
            Limit                     = 1,
            ProjectionExpression      = "run_id, #st",
            ExpressionAttributeNames  = new Dictionary<string, string> { ["#st"] = "status" }
        });

        if (resp.Items.Count == 0)
            throw new Exception($"No sync records found for URL: {normalizedUrl}");

        runId = resp.Items[0]["run_id"].S;
        string status = resp.Items[0].TryGetValue("status", out AttributeValue? sv) ? sv.S ?? "?" : "?";
        Console.WriteLine($"Latest run_id: {runId}  (status={status})");
    }

    string cleanupMessage = JsonSerializer.Serialize(new
    {
        source_url     = normalizedUrl,
        current_run_id = runId
    });

    Console.WriteLine();
    Console.WriteLine($"URL          : {normalizedUrl}");
    Console.WriteLine($"current_run_id: {runId}");
    Console.WriteLine($"Message      : {cleanupMessage}");
    Console.WriteLine($"Mode         : {(confirm ? "LIVE — will send to cleanup queue" : "DRY RUN — pass --confirm to send")}");

    if (!confirm) return;

    var sqs          = BuildSqsClient();
    var cleanupQueue = (await sqs.GetQueueUrlAsync("iran-conflict-map-cleanup.fifo")).QueueUrl;

    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl       = cleanupQueue,
        MessageBody    = cleanupMessage,
        MessageGroupId = "cleanup"
    });

    Console.WriteLine();
    Console.WriteLine("Cleanup message sent. The cleanup Lambda will discard stale review items shortly.");
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

// ── enrich-review ─────────────────────────────────────────────────────────────

async Task EnrichReview(string[] opts)
{
    var confirm = opts.Contains("--confirm");
    const string reviewName = "iran-conflict-map-review.fifo";
    const string tableName  = "strikes";
    const string indexName  = "entity-date-index";

    var sqs       = BuildSqsClient();
    var dynamo    = BuildDynamoClient();
    var reviewUrl = (await sqs.GetQueueUrlAsync(reviewName)).QueueUrl;

    Console.WriteLine($"Review queue : {reviewUrl}");
    Console.WriteLine($"Mode         : {(confirm ? "LIVE — will re-enqueue enriched messages" : "DRY RUN — pass --confirm to apply")}");
    Console.WriteLine();

    // ── 1. Drain all messages from the queue ─────────────────────────────────
    var all          = new List<Message>();
    var received     = new HashSet<string>();
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
        emptyRetries = newCount == 0 ? emptyRetries + 1 : 0;
    }

    Console.WriteLine($"Found {all.Count} message(s) in review queue.");

    // ── 2. Classify each message ──────────────────────────────────────────────
    var needsEnrich = new List<Message>();
    var others      = new List<Message>();

    foreach (var msg in all)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(msg.Body).RootElement; }
        catch { Console.WriteLine($"  [skip] {msg.MessageId} — unparseable JSON"); others.Add(msg); continue; }

        var hasWrapper = root.TryGetProperty("item", out JsonElement data);
        if (!hasWrapper) data = root;

        bool hasAsNew    = data.TryGetProperty("as_new",    out var an) && an.ValueKind != JsonValueKind.Null;
        bool hasAsUpdate = data.TryGetProperty("as_update", out var au) && au.ValueKind != JsonValueKind.Null;
        bool hasNearest  = data.TryGetProperty("nearest_record", out var nr) && nr.ValueKind != JsonValueKind.Null;

        if (hasAsNew && hasAsUpdate && !hasNearest)
        {
            needsEnrich.Add(msg);
            Console.WriteLine($"  [enrich]  {msg.MessageId}");
        }
        else
        {
            others.Add(msg);
            Console.WriteLine($"  [skip]    {msg.MessageId} — already has nearest_record or not ambiguous");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"To enrich: {needsEnrich.Count}  Skipping: {others.Count}");

    // Release messages we're not touching
    foreach (var msg in others.Concat(confirm ? Enumerable.Empty<Message>() : needsEnrich))
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

    if (!confirm)
    {
        Console.WriteLine("Re-run with --confirm to enrich.");
        return;
    }

    // ── 3. Enrich and re-enqueue ──────────────────────────────────────────────
    int enriched = 0;

    foreach (var msg in needsEnrich)
    {
        JsonElement root    = JsonDocument.Parse(msg.Body).RootElement;
        var hasWrapper      = root.TryGetProperty("item", out JsonElement data);
        if (!hasWrapper) data = root;

        root.TryGetProperty("source_url", out var sourceUrl);
        root.TryGetProperty("sync_id",    out var syncId);
        data.TryGetProperty("note",       out var note);
        data.TryGetProperty("as_new",     out var asNew);
        data.TryGetProperty("as_update",  out var asUpdate);

        // Extract lat/lng/date from as_update.lookup
        object? nearestRecord = null;
        if (asUpdate.TryGetProperty("lookup", out JsonElement lookup))
        {
            double? lat  = lookup.TryGetProperty("lat",  out var latEl)  && latEl.ValueKind  == JsonValueKind.Number ? latEl.GetDouble()        : null;
            double? lng  = lookup.TryGetProperty("lng",  out var lngEl)  && lngEl.ValueKind  == JsonValueKind.Number ? lngEl.GetDouble()        : null;
            string? date = lookup.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String ? dateEl.GetString()       : null;

            if (lat != null && lng != null && date != null)
                nearestRecord = await FindNearestSimplifiedAsync(dynamo, tableName, indexName, date, lat.Value, lng.Value);
        }

        string newBody = JsonSerializer.Serialize(new
        {
            source_url = sourceUrl.ValueKind == JsonValueKind.Undefined ? null : (object?)sourceUrl,
            sync_id    = syncId.ValueKind    == JsonValueKind.Undefined ? null : (object?)syncId,
            item       = new
            {
                note           = note.ValueKind == JsonValueKind.Undefined ? null : note.GetString(),
                as_new         = (object?)asNew,
                as_update      = (object?)asUpdate,
                nearest_record = nearestRecord
            }
        });

        await SendReviewMessage(sqs, reviewUrl, newBody);
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = reviewUrl, ReceiptHandle = msg.ReceiptHandle });

        string nearestId = nearestRecord is Dictionary<string, object?> d && d.TryGetValue("id", out object? id) ? $" → nearest id={id}" : " → no match found";
        Console.WriteLine($"  enriched {msg.MessageId}{nearestId}");
        enriched++;
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {enriched} message(s) enriched.");
}

async Task<Dictionary<string, object?>?> FindNearestSimplifiedAsync(
    IAmazonDynamoDB dynamo, string tableName, string indexName,
    string date, double lat, double lng)
{
    DateTime parsedDate   = DateTime.Parse(date);
    string[] datesToQuery = new[]
    {
        parsedDate.AddDays(-1).ToString("yyyy-MM-dd"),
        date,
        parsedDate.AddDays(1).ToString("yyyy-MM-dd")
    };

    var allCandidates = new List<Dictionary<string, AttributeValue>>();
    foreach (string queryDate in datesToQuery)
    {
        var resp = await dynamo.QueryAsync(new QueryRequest
        {
            TableName                 = tableName,
            IndexName                 = indexName,
            KeyConditionExpression    = "entity = :entity AND #d = :date",
            ExpressionAttributeNames  = new Dictionary<string, string> { ["#d"] = "date" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":entity"] = new AttributeValue { S = "strike" },
                [":date"]   = new AttributeValue { S = queryDate }
            }
        });
        allCandidates.AddRange(resp.Items);
    }

    var nearest = allCandidates
        .Where(i => i.ContainsKey("lat") && i.ContainsKey("lng"))
        .Select(i => (item: i, dist: ReviewHaversineKm(lat, lng, double.Parse(i["lat"].N), double.Parse(i["lng"].N))))
        .OrderBy(x => x.dist)
        .FirstOrDefault();

    if (nearest.item == null) return null;

    var result = new Dictionary<string, object?>();
    foreach (var (k, v) in nearest.item)
    {
        if (v.S != null)      result[k] = v.S;
        else if (v.N != null) result[k] = v.N;
        else if (v.IsBOOLSet) result[k] = v.BOOL;
    }
    return result;
}

static double ReviewHaversineKm(double lat1, double lon1, double lat2, double lon2)
{
    const double R = 6371.0;
    double dLat = (lat2 - lat1) * Math.PI / 180.0;
    double dLon = (lon2 - lon1) * Math.PI / 180.0;
    double a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}

// ── backfill-economic ─────────────────────────────────────────────────────────

async Task BackfillEconomic(string[] opts)
{
    string startStr = GetFlag(ref opts, "--start") ?? "2026-02-28";
    string endStr   = GetFlag(ref opts, "--end")   ?? DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
    string? eiaKey  = GetFlag(ref opts, "--eia-key");
    bool confirm    = opts.Contains("--confirm");

    if (string.IsNullOrEmpty(eiaKey))
    {
        Console.WriteLine("ERROR: --eia-key is required");
        Console.WriteLine("Usage: backfill-economic --start yyyy-MM-dd --end yyyy-MM-dd --eia-key <key> [--confirm]");
        return;
    }

    if (!DateTime.TryParse(startStr, out DateTime startDate) || !DateTime.TryParse(endStr, out DateTime endDate))
    {
        Console.WriteLine("ERROR: --start and --end must be valid dates (yyyy-MM-dd)");
        return;
    }

    if (startDate > endDate)
    {
        Console.WriteLine("ERROR: --start must be before or equal to --end");
        return;
    }

    const string tableName = "iran-conflict-map-brent-prices";

    // Fetch from EIA
    Console.WriteLine($"Fetching Brent prices from EIA: {startStr} → {endStr}");
    using HttpClient http = new();
    // Brackets must be percent-encoded — .NET Uri will double-encode literal brackets
    // EIA v2 spot prices for Brent are weekly only (no daily series available)
    string eiaUrl = $"https://api.eia.gov/v2/petroleum/pri/spt/data/" +
                    $"?api_key={eiaKey}" +
                    $"&frequency=weekly" +
                    $"&data%5B0%5D=value" +
                    $"&facets%5Bproduct%5D%5B%5D=EPCBRENT" +
                    $"&sort%5B0%5D%5Bcolumn%5D=period" +
                    $"&sort%5B0%5D%5Bdirection%5D=desc" +
                    $"&length=500";

    HttpResponseMessage eiaResponse = await http.GetAsync(eiaUrl);
    eiaResponse.EnsureSuccessStatusCode();
    string eiaJson = await eiaResponse.Content.ReadAsStringAsync();

    using JsonDocument doc = JsonDocument.Parse(eiaJson);
    JsonElement dataArr = doc.RootElement.GetProperty("response").GetProperty("data");

    List<(string date, decimal price)> rows = new();
    foreach (JsonElement row in dataArr.EnumerateArray())
    {
        string period = row.GetProperty("period").GetString()!;
        // EIA start/end params are unreliable for weekly series — filter in memory
        if (string.Compare(period, startStr, StringComparison.Ordinal) < 0) continue;
        if (string.Compare(period, endStr,   StringComparison.Ordinal) > 0) continue;
        // EIA returns value as a JSON string (e.g. "111.4"), not a number
        if (row.TryGetProperty("value", out JsonElement valEl) && valEl.ValueKind != JsonValueKind.Null)
        {
            decimal price = valEl.ValueKind == JsonValueKind.Number
                ? valEl.GetDecimal()
                : decimal.Parse(valEl.GetString()!);
            rows.Add((period, price));
        }
    }

    Console.WriteLine($"EIA returned {rows.Count} trading day(s)");
    if (rows.Count == 0)
    {
        Console.WriteLine("Nothing to write.");
        return;
    }

    foreach ((string date, decimal price) in rows)
        Console.WriteLine($"  {date}  ${price:F2}");

    if (!confirm)
    {
        Console.WriteLine();
        Console.WriteLine($"Dry run — would write {rows.Count} row(s) to {tableName}. Pass --confirm to execute.");
        return;
    }

    // Write to DynamoDB — one row per trading day, sentinel midnight timestamp
    IAmazonDynamoDB dynamo = BuildDynamoClient();
    int written = 0, skipped = 0;

    foreach ((string date, decimal price) in rows)
    {
        string timestamp = $"{date}T00:00:00Z";   // sentinel: historical settlement
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName           = tableName,
                ConditionExpression = "attribute_not_exists(#ts)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#ts"] = "timestamp" },
                Item = new Dictionary<string, AttributeValue>
                {
                    ["date"]        = new AttributeValue { S = date },
                    ["timestamp"]   = new AttributeValue { S = timestamp },
                    ["brent_price"] = new AttributeValue { N = price.ToString("F2") },
                    ["currency"]    = new AttributeValue { S = "USD" },
                }
            });
            Console.WriteLine($"  wrote  {date}  ${price:F2}");
            written++;
        }
        catch (ConditionalCheckFailedException)
        {
            Console.WriteLine($"  skip   {date}  (row already exists)");
            skipped++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {written} written, {skipped} skipped.");
}

// ── backfill-signals ──────────────────────────────────────────────────────────

async Task BackfillSignals(string[] opts)
{
    string startStr      = GetFlag(ref opts, "--start") ?? "2025-10-07";
    string endStr        = GetFlag(ref opts, "--end")   ?? DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
    string? anthropicKey = GetFlag(ref opts, "--anthropic-key");
    bool confirm         = opts.Contains("--confirm");

    if (string.IsNullOrEmpty(anthropicKey))
    {
        Console.WriteLine("ERROR: --anthropic-key is required");
        Console.WriteLine("Usage: backfill-signals [--start yyyy-MM-dd] [--end yyyy-MM-dd] --anthropic-key <key> [--confirm]");
        return;
    }

    if (!DateTime.TryParse(startStr, out DateTime startDate) || !DateTime.TryParse(endStr, out DateTime endDate))
    {
        Console.WriteLine("ERROR: --start and --end must be valid dates (yyyy-MM-dd)");
        return;
    }

    const string syncsTableName   = "syncs-v2";
    const string signalsTableName = "iran-conflict-map-economic-signals";

    IAmazonDynamoDB dynamo = BuildDynamoClient();

    // ── 1. Collect unique report URLs from syncs-v2 ───────────────────────
    Console.WriteLine($"Scanning syncs-v2 for sync runs between {startStr} and {endStr}...");

    // Scan all pages — filter in memory by run_id date prefix (ISO 8601).
    // No status filter: include any run that landed in the window (processing, complete, etc.)
    // to avoid missing reports where the final status string differs from expectations.
    List<string> reportUrls = new();
    Dictionary<string, AttributeValue>? lastKey = null;
    do
    {
        ScanResponse scan = await dynamo.ScanAsync(new ScanRequest
        {
            TableName         = syncsTableName,
            ExclusiveStartKey = lastKey
        });

        foreach (Dictionary<string, AttributeValue> item in scan.Items)
        {
            if (!item.ContainsKey("run_id") || !item.ContainsKey("report_url")) continue;
            // Skip error/fetch_error runs — they have no processed content
            if (item.ContainsKey("status") && item["status"].S is "error" or "fetch_error" or "claude_error") continue;
            string runId = item["run_id"].S;
            if (runId.Length < 10) continue;
            string runDate = runId[..10];
            if (string.Compare(runDate, startStr, StringComparison.Ordinal) < 0) continue;
            if (string.Compare(runDate, endStr,   StringComparison.Ordinal) > 0) continue;
            string url = item["report_url"].S;
            if (!reportUrls.Contains(url))
                reportUrls.Add(url);
        }

        lastKey = scan.LastEvaluatedKey?.Count > 0 ? scan.LastEvaluatedKey : null;
    } while (lastKey != null);

    if (reportUrls.Count == 0)
    {
        Console.WriteLine("No sync runs found in that date range. Nothing to backfill.");
        return;
    }

    Console.WriteLine($"Found {reportUrls.Count} unique report URL(s) to process:");
    foreach (string u in reportUrls)
        Console.WriteLine($"  {u}");
    Console.WriteLine();

    if (!confirm)
    {
        Console.WriteLine($"Dry run — would call Claude for {reportUrls.Count} report(s) to extract economic signals. Pass --confirm to execute.");
        return;
    }

    // ── 2. Fetch each report and extract economic signals via Claude ───────
    using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    const string economicPrompt = """
        I'm analysing a CTP-ISW Iran update report for economic signals only. Do NOT extract any conflict events.

        Extract the following fields:

        - date — YYYY-MM-DD of the report
        - hormuz_status — "no_alert" if the report is silent on Hormuz; "open" if the report explicitly confirms normal/open passage at the Strait; "restricted" if it explicitly describes interference, seizures, mining, blockade activity, or legislative/military actions asserting control over the Strait; "closed" if it describes a full blockade or closure
        - economic_notes — flat array of concise complete sentences, one per distinct signal. Include: active or newly-announced sanctions and designations, OFAC/Treasury actions, energy infrastructure damage or threats, oil/gas price mentions tied to conflict activity, shipping insurance or Lloyd's notices, export bans or waivers, financial system impacts (SWIFT, correspondent banking). Empty array [] if nothing qualifies.

        Return a single JSON object and NOTHING ELSE:
        {
          "date": "YYYY-MM-DD",
          "hormuz_status": "no_alert",
          "economic_notes": ["..."]
        }
        """;

    int written = 0, skipped = 0, noData = 0, errors = 0;

    foreach (string reportUrl in reportUrls)
    {
        Console.WriteLine($"Processing: {reportUrl}");
        try
        {
            // Fetch report
            HttpResponseMessage fetchResp = await http.GetAsync(reportUrl);
            if (!fetchResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  WARN: fetch returned {(int)fetchResp.StatusCode} — skipping");
                errors++;
                continue;
            }
            string html = await fetchResp.Content.ReadAsStringAsync();
            string text = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(\r?\n){3,}", "\n\n").Trim();
            if (text.Length > 80_000) text = text[..80_000];

            // Call Claude
            string requestBody = JsonSerializer.Serialize(new
            {
                model      = "claude-haiku-4-5-20251001",
                max_tokens = 2048,
                system     = economicPrompt,
                messages   = new[] { new { role = "user", content = $"Report URL: {reportUrl}\n\n{text}" } }
            });

            using HttpRequestMessage req = new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new System.Net.Http.StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", anthropicKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            HttpResponseMessage claudeResp = await http.SendAsync(req);
            string claudeBody = await claudeResp.Content.ReadAsStringAsync();

            if (!claudeResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  WARN: Claude API error {(int)claudeResp.StatusCode}: {claudeBody[..Math.Min(claudeBody.Length, 200)]}");
                errors++;
                continue;
            }

            using JsonDocument claudeDoc = JsonDocument.Parse(claudeBody);
            string rawText = claudeDoc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

            int start = rawText.IndexOf('{');
            if (start == -1)
            {
                Console.WriteLine($"  WARN: no JSON in Claude response: {rawText[..Math.Min(rawText.Length, 300)]}");
                errors++;
                continue;
            }

            // Walk to find balanced closing brace (same approach as Sync Lambda)
            int depth = 0; bool inStr = false; bool esc = false; int end = -1;
            for (int i = start; i < rawText.Length; i++)
            {
                char c = rawText[i];
                if (esc)                       { esc = false; continue; }
                if (c == '\\' && inStr)        { esc = true;  continue; }
                if (c == '"')                  { inStr = !inStr; continue; }
                if (inStr)                       continue;
                if      (c == '{')             depth++;
                else if (c == '}' && --depth == 0) { end = i; break; }
            }

            if (end == -1)
            {
                Console.WriteLine($"  WARN: unbalanced JSON braces in Claude response — skipping");
                errors++;
                continue;
            }

            using JsonDocument result = JsonDocument.Parse(rawText[start..(end + 1)]);
            JsonElement root = result.RootElement;

            string date = root.TryGetProperty("date", out JsonElement dateEl) && dateEl.ValueKind == JsonValueKind.String
                ? dateEl.GetString()!
                : DateTime.UtcNow.ToString("yyyy-MM-dd");

            string hormuzStatus = root.TryGetProperty("hormuz_status", out JsonElement hormuzEl) && hormuzEl.ValueKind == JsonValueKind.String
                ? hormuzEl.GetString()!
                : "no_alert";

            // Build DynamoDB item
            Dictionary<string, AttributeValue> item = new()
            {
                ["date"]          = new AttributeValue { S = date },
                ["source_url"]    = new AttributeValue { S = reportUrl },
                ["entity"]        = new AttributeValue { S = "signal" },
                ["hormuz_status"] = new AttributeValue { S = hormuzStatus }
            };

            if (root.TryGetProperty("economic_notes", out JsonElement notesEl) && notesEl.ValueKind == JsonValueKind.Array)
            {
                List<AttributeValue> noteValues = notesEl.EnumerateArray()
                    .Where(n => n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                    .Select(n => new AttributeValue { S = n.GetString()! })
                    .ToList();
                if (noteValues.Count > 0)
                    item["economic_notes"] = new AttributeValue { L = noteValues };
            }

            // Print preview
            Console.WriteLine($"  date={date}  hormuz={hormuzStatus}");
            if (item.ContainsKey("economic_notes"))
                foreach (AttributeValue n in item["economic_notes"].L)
                    Console.WriteLine($"  note: {n.S}");

            // Write (idempotent — overwrites same date+source_url if re-run)
            await dynamo.PutItemAsync(new PutItemRequest { TableName = signalsTableName, Item = item });
            Console.WriteLine($"  wrote {date}");
            written++;

            await Task.Delay(500);   // rate-limit Claude API calls
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
            errors++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {written} written, {noData} no-data, {skipped} skipped, {errors} errors.");
}

// ── stamp-signal-entity ───────────────────────────────────────────────────────

async Task StampSignalEntity(string[] opts)
{
    bool confirm = opts.Contains("--confirm");
    const string tableName = "iran-conflict-map-economic-signals";

    IAmazonDynamoDB dynamo = BuildDynamoClient();

    // Scan for items missing entity attribute
    List<Dictionary<string, AttributeValue>> toUpdate = new();
    Dictionary<string, AttributeValue>? lastKey = null;
    do
    {
        ScanResponse scan = await dynamo.ScanAsync(new ScanRequest
        {
            TableName         = tableName,
            ExclusiveStartKey = lastKey,
            FilterExpression  = "attribute_not_exists(entity)",
            ProjectionExpression = "#d, source_url",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#d"] = "date" }
        });
        toUpdate.AddRange(scan.Items);
        lastKey = scan.LastEvaluatedKey?.Count > 0 ? scan.LastEvaluatedKey : null;
    } while (lastKey != null);

    Console.WriteLine($"Found {toUpdate.Count} row(s) missing entity attribute.");
    if (toUpdate.Count == 0) return;

    foreach (Dictionary<string, AttributeValue> row in toUpdate)
        Console.WriteLine($"  {row["date"].S}  {row["source_url"].S}");

    if (!confirm)
    {
        Console.WriteLine($"\nDry run — pass --confirm to stamp entity='signal' on {toUpdate.Count} row(s).");
        return;
    }

    int updated = 0;
    foreach (Dictionary<string, AttributeValue> row in toUpdate)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["date"]       = new AttributeValue { S = row["date"].S },
                ["source_url"] = new AttributeValue { S = row["source_url"].S }
            },
            UpdateExpression = "SET entity = :e",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":e"] = new AttributeValue { S = "signal" }
            }
        });
        Console.WriteLine($"  stamped {row["date"].S}");
        updated++;
    }

    Console.WriteLine($"\nDone. {updated} row(s) updated.");
}

// ── normalize-actors ─────────────────────────────────────────────────────────
async Task NormalizeActors(string[] opts)
{
    bool confirm = opts.Contains("--confirm");
    const string tableName = "strikes";

    // Canonical actor list — maps every known variant to its standard name
    Dictionary<string, string> actorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // US
        ["US"]                         = "US Central Command",
        ["United States"]              = "US Central Command",
        ["US/CENTCOM"]                 = "US Central Command",

        // Israel
        ["IDF"]                        = "Israel Defense Forces",
        ["Israel"]                     = "Israel Defense Forces",

        // Combined
        ["US, Israel"]                 = "US-Israel Combined Force",
        ["US-Israel"]                  = "US-Israel Combined Force",
        ["US-Israel combined force"]   = "US-Israel Combined Force",
        ["US/Israel"]                  = "US-Israel Combined Force",
        ["US/Israel (combined force)"] = "US-Israel Combined Force",
        ["US/Israeli combined force"]  = "US-Israel Combined Force",

        // IRGC
        ["Iran/IRGC"]                  = "Islamic Revolutionary Guard Corps",
        ["Islamic Revolutionary Guard Corps / Iranian-backed militias"] = "Islamic Revolutionary Guard Corps",

        // Iran (conventional / unspecified)
        ["Iran"]                       = "Iran",

        // Lebanese Hezbollah
        ["Hezbollah"]                  = "Lebanese Hezbollah",

        // Iraqi militias — all variants roll up to canonical
        ["Iran-backed Iraqi militia"]                     = "Iranian-backed Iraqi Militias",
        ["Iran-backed Iraqi militias"]                    = "Iranian-backed Iraqi Militias",
        ["Iran / Iranian-backed Iraqi Militias"]          = "Iranian-backed Iraqi Militias",
        ["Iranian-backed Iraqi militia"]                  = "Iranian-backed Iraqi Militias",
        ["Iranian-backed Iraqi militias"]                 = "Iranian-backed Iraqi Militias",
        ["Iranian-backed Iraqi Militias (unidentified)"]  = "Iranian-backed Iraqi Militias",
        ["Iranian-backed Iraqi Militias (Kataib Sarkhat al Quds / Harakat Hezbollah al Nujaba)"] = "Iranian-backed Iraqi Militias",
        ["Iranian-backed Iraqi Militias (Rijal al Baas al Shadid front group)"]                  = "Iranian-backed Iraqi Militias",
        ["Islamic Resistance in Iraq"]                    = "Iranian-backed Iraqi Militias",
        ["Islamic Resistance of Iraq"]                    = "Iranian-backed Iraqi Militias",
        ["Kataib Hezbollah"]                              = "Iranian-backed Iraqi Militias",
        ["Harakat Hezbollah al Nujaba / Kataib Hezbollah"] = "Iranian-backed Iraqi Militias",
        ["Kataib Jund al Karar"]                          = "Iranian-backed Iraqi Militias",
        ["Kataib Sarkhat al Quds (Harakat Hezbollah al Nujaba front)"]       = "Iranian-backed Iraqi Militias",
        ["Kataib Sarkhat al Quds (Iranian-backed Iraqi militia)"]            = "Iranian-backed Iraqi Militias",
        ["Kataib Sarqhat al Quds (Harakat Hezbollah al Nujaba front group)"] = "Iranian-backed Iraqi Militias",
        ["Saraya Awliya al Dam"]                                             = "Iranian-backed Iraqi Militias",
        ["Saraya Awliya al Dam (Iran-backed Iraqi militia)"]                 = "Iranian-backed Iraqi Militias",
        ["Saraya Awliya al Dam (Kataib Sayyid al Shuhada front group)"]      = "Iranian-backed Iraqi Militias",
        ["Jaysh al Ghadab (Iran-backed Iraqi militia front group)"]          = "Iranian-backed Iraqi Militias",
        ["Jaysh al Ghadab (Iranian-backed Iraqi militia)"]                   = "Iranian-backed Iraqi Militias",

        // Houthis
        ["Houthi"]                     = "Houthi Movement",
    };

    IAmazonDynamoDB dynamo = BuildDynamoClient();

    // Scan all strikes using the entity-date GSI
    List<(Dictionary<string, AttributeValue> key, string oldActor, string newActor)> toUpdate = new();
    Dictionary<string, AttributeValue>? lastKey = null;

    do
    {
        QueryResponse resp = await dynamo.QueryAsync(new QueryRequest
        {
            TableName                 = tableName,
            IndexName                 = "entity-date-index",
            KeyConditionExpression    = "entity = :e",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":e"] = new AttributeValue { S = "strike" }
            },
            ProjectionExpression      = "id, actor",
            ExclusiveStartKey         = lastKey
        });

        foreach (Dictionary<string, AttributeValue> item in resp.Items)
        {
            if (!item.ContainsKey("actor") || !item.ContainsKey("id")) continue;
            string current = item["actor"].S;
            if (actorMap.TryGetValue(current, out string? canonical) && canonical != current)
            {
                toUpdate.Add((
                    new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue { S = item["id"].S } },
                    current,
                    canonical
                ));
            }
        }

        lastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
    } while (lastKey != null);

    if (toUpdate.Count == 0)
    {
        Console.WriteLine("No actor values need normalization.");
        return;
    }

    Console.WriteLine($"Found {toUpdate.Count} strike(s) to normalize:\n");
    foreach ((Dictionary<string, AttributeValue> _, string old, string next) in toUpdate)
        Console.WriteLine($"  \"{old}\" → \"{next}\"");

    if (!confirm)
    {
        Console.WriteLine($"\nDry run — pass --confirm to update {toUpdate.Count} row(s).");
        return;
    }

    int updated = 0;
    foreach ((Dictionary<string, AttributeValue> key, string _, string newActor) in toUpdate)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName        = tableName,
            Key              = key,
            UpdateExpression = "SET actor = :a",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":a"] = new AttributeValue { S = newActor }
            }
        });
        updated++;
    }

    Console.WriteLine($"\nDone. {updated} row(s) updated.");
}

// ── backfill-report-date ──────────────────────────────────────────────────────

async Task BackfillReportDate(string[] opts)
{
    bool confirm = opts.Contains("--confirm");
    const string tableName = "syncs-v2";

    IAmazonDynamoDB dynamo = BuildDynamoClient();

    Console.WriteLine($"Table : {tableName}");
    Console.WriteLine($"Mode  : {(confirm ? "LIVE — will write report_date" : "DRY RUN — pass --confirm to apply")}");
    Console.WriteLine();

    // Scan all records
    var items    = new List<Dictionary<string, AttributeValue>>();
    Dictionary<string, AttributeValue>? lastKey = null;
    do
    {
        var req = new ScanRequest
        {
            TableName                 = tableName,
            ProjectionExpression      = "report_url, run_id, report_date",
            ExclusiveStartKey         = lastKey
        };
        ScanResponse resp = await dynamo.ScanAsync(req);
        items.AddRange(resp.Items);
        lastKey = resp.LastEvaluatedKey.Count > 0 ? resp.LastEvaluatedKey : null;
    }
    while (lastKey != null);

    Console.WriteLine($"Found {items.Count} sync record(s).");
    Console.WriteLine();

    int alreadySet = 0, willUpdate = 0, cannotParse = 0;

    foreach (Dictionary<string, AttributeValue> item in items)
    {
        string reportUrl = item.TryGetValue("report_url", out AttributeValue? urlAttr) ? urlAttr.S ?? "" : "";
        string runId     = item.TryGetValue("run_id",     out AttributeValue? ridAttr) ? ridAttr.S ?? "" : "";

        string? parsed = ParseReportDate(reportUrl);

        string existing = item.TryGetValue("report_date", out AttributeValue? rdAttr) ? rdAttr.S ?? "" : "";

        if (parsed == null)
        {
            cannotParse++;
            Console.WriteLine($"  [skip/no-parse] run={runId[..Math.Min(runId.Length, 24)]}  url={reportUrl}");
            continue;
        }

        if (existing == parsed)
        {
            alreadySet++;
            Console.WriteLine($"  [ok]     run={runId[..Math.Min(runId.Length, 24)]}  report_date={parsed}");
            continue;
        }

        string marker = existing == "" ? "[add]    " : "[update] ";
        Console.WriteLine($"  {marker} run={runId[..Math.Min(runId.Length, 24)]}  report_date={parsed}{(existing != "" ? $"  (was {existing})" : "")}");
        willUpdate++;

        if (confirm)
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName        = tableName,
                Key              = new Dictionary<string, AttributeValue>
                {
                    ["report_url"] = new() { S = reportUrl },
                    ["run_id"]     = new() { S = runId }
                },
                UpdateExpression          = "SET report_date = :d",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":d"] = new() { S = parsed }
                }
            });
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Already correct : {alreadySet}");
    Console.WriteLine($"Updated         : {willUpdate}{(confirm ? "" : " (dry run)")}");
    Console.WriteLine($"Could not parse : {cannotParse}");

    if (!confirm && willUpdate > 0)
        Console.WriteLine("\nRe-run with --confirm to apply.");
}

static string? ParseReportDate(string? url)
{
    if (string.IsNullOrEmpty(url)) return null;

    System.Text.RegularExpressions.Match m =
        System.Text.RegularExpressions.Regex.Match(url, @"([a-z]+)-(\d{1,2})-(\d{4})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success) return null;

    var months = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = "01", ["february"] = "02", ["march"]     = "03",
        ["april"]   = "04", ["may"]       = "05", ["june"]      = "06",
        ["july"]    = "07", ["august"]    = "08", ["september"] = "09",
        ["october"] = "10", ["november"]  = "11", ["december"]  = "12"
    };

    if (!months.TryGetValue(m.Groups[1].Value, out string? month)) return null;

    string day  = m.Groups[2].Value.PadLeft(2, '0');
    string year = m.Groups[3].Value;
    return $"{year}-{month}-{day}";
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

string NormalizeReportUrl(string url)
{
    var uri = new Uri(url.Trim());
    return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()}";
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
