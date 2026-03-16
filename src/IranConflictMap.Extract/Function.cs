using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using MimeKit;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Extract;

public class Function
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IAmazonS3  _s3;
    private readonly IAmazonSQS _sqs;

    private static readonly string EmailBucket    = Env("EMAIL_BUCKET",       "");
    private static readonly string InboxPrefix    = Env("EMAIL_INBOX_PREFIX", "inbox/");
    private static readonly string OtherPrefix    = Env("EMAIL_OTHER_PREFIX", "other/");
    private static readonly string ReportQueueUrl = Env("REPORT_QUEUE_URL",   "");

    private const string ExpectedSender   = "criticalthreats@aei.org";
    private const string ExpectedKeyword1 = "Iran";
    private const string ExpectedKeyword2 = "Update";
    private const string ReportBaseUrl    = "https://www.criticalthreats.org/analysis/";

    public Function()
    {
        _s3  = new AmazonS3Client();
        _sqs = new AmazonSQSClient();
    }

    public Function(IAmazonS3 s3, IAmazonSQS sqs)
    {
        _s3  = s3;
        _sqs = sqs;
    }

    // Handles both EventBridge S3 ObjectCreated events (automated) and
    // direct Lambda invocations with an empty/null payload (manual trigger → scan inbox).
    public async Task FunctionHandler(JsonElement input, ILambdaContext context)
    {
        if (input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("detail", out var detail) &&
            detail.TryGetProperty("object", out var obj) &&
            obj.TryGetProperty("key", out var keyEl))
        {
            // EventBridge S3 event — process single email
            var emailKey = keyEl.GetString() ?? throw new Exception("Empty object key in S3 event");
            await ProcessEmail(emailKey, context);
        }
        else
        {
            // Manual trigger — scan entire inbox
            context.Logger.LogLine("[extract] manual trigger — scanning inbox");
            await ScanInbox(context);
        }
    }

    // ── Inbox scan (manual trigger) ──────────────────────────────────────────

    private async Task ScanInbox(ILambdaContext ctx)
    {
        var listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = EmailBucket,
            Prefix     = InboxPrefix
        });

        var objects = listResp.S3Objects
            .Where(o => o.Key != InboxPrefix)
            .OrderBy(o => o.LastModified)
            .ToList();

        ctx.Logger.LogLine($"[extract] {objects.Count} objects in inbox");

        foreach (var obj in objects)
            await ProcessEmail(obj.Key, ctx);
    }

    // ── Single email processing ───────────────────────────────────────────────

    private async Task ProcessEmail(string emailKey, ILambdaContext ctx)
    {
        ctx.Logger.LogLine($"[extract] processing: {emailKey}");

        var message = await ReadMimeMessage(emailKey, ctx);
        if (message == null) return;

        var (isMatch, reason) = IsCtpIswEmail(message);
        if (!isMatch)
        {
            var from    = message.From.Mailboxes.FirstOrDefault()?.Address ?? "(unknown)";
            var subject = message.Subject ?? "";
            ctx.Logger.LogLine($"[extract] not CTP-ISW, moving to other/: from={from} subject='{subject[..Math.Min(subject.Length, 60)]}'");
            await MoveS3Object(emailKey, OtherPrefix + emailKey[InboxPrefix.Length..], ctx);
            return;
        }

        ctx.Logger.LogLine($"[extract] matched ({reason}): '{message.Subject}'");

        var reportUrl = BuildReportUrl(message.Subject ?? "");
        if (reportUrl == null)
        {
            ctx.Logger.LogLine($"[extract] could not construct report URL from subject: '{message.Subject}' — leaving in inbox");
            return;
        }

        ctx.Logger.LogLine($"[extract] verifying: {reportUrl}");
        if (!await VerifyReportUrl(reportUrl, ctx))
        {
            ctx.Logger.LogLine($"[extract] URL not found: {reportUrl} — leaving in inbox");
            return;
        }

        await EnqueueReportUrl(reportUrl, emailKey, ctx);
    }

    // ── URL construction from email subject ───────────────────────────────────

    private static readonly string[] MonthNames =
    {
        "january","february","march","april","may","june",
        "july","august","september","october","november","december"
    };

    internal static string? BuildReportUrl(string subject)
    {
        // Match "Some Title: Month D, YYYY" (time zone suffix is ignored)
        var re = new Regex(
            @"^(.+?):\s*(january|february|march|april|may|june|july|august|september|october|november|december)\s+(\d{1,2}),\s+(\d{4})",
            RegexOptions.IgnoreCase);

        var m = re.Match(subject);
        if (!m.Success) return null;

        var titlePart = m.Groups[1].Value.Trim();
        var month     = m.Groups[2].Value.ToLowerInvariant();
        var day       = int.Parse(m.Groups[3].Value);
        var year      = m.Groups[4].Value;

        // Slugify: lowercase, collapse non-alphanumeric runs to single hyphen
        var slug = Regex.Replace(titlePart.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        return $"{ReportBaseUrl}{slug}-{month}-{day}-{year}";
    }

    private async Task<bool> VerifyReportUrl(string url, ILambdaContext ctx)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (compatible)");
            var resp = await Http.SendAsync(req);
            ctx.Logger.LogLine($"[extract] HEAD {url} → {(int)resp.StatusCode}");
            // 200 with non-"Not Found" title = valid; we'll rely on status code only
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[extract] verify error: {ex.Message}");
            return false;
        }
    }

    private async Task EnqueueReportUrl(string url, string emailKey, ILambdaContext ctx)
    {
        var body = JsonSerializer.Serialize(new { url, email_key = emailKey });

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl       = ReportQueueUrl,
            MessageBody    = body,
            MessageGroupId = "report"
        });

        ctx.Logger.LogLine($"[extract] enqueued → {url}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<MimeMessage?> ReadMimeMessage(string key, ILambdaContext ctx)
    {
        try
        {
            var resp = await _s3.GetObjectAsync(EmailBucket, key);
            return await MimeMessage.LoadAsync(resp.ResponseStream);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogLine($"[extract] failed to read {key}: {ex.Message}");
            return null;
        }
    }

    private static (bool match, string reason) IsCtpIswEmail(MimeMessage message)
    {
        var from    = message.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var subject = message.Subject ?? "";

        if (!from.Equals(ExpectedSender, StringComparison.OrdinalIgnoreCase))
            return (false, "");

        if (subject.Contains(ExpectedKeyword1, StringComparison.OrdinalIgnoreCase) &&
            subject.Contains(ExpectedKeyword2, StringComparison.OrdinalIgnoreCase))
            return (true, "sender+subject");

        var body = message.TextBody ?? Regex.Replace(message.HtmlBody ?? "", @"<[^>]+>", " ");
        if (body.Contains(ExpectedKeyword1, StringComparison.OrdinalIgnoreCase) &&
            body.Contains(ExpectedKeyword2, StringComparison.OrdinalIgnoreCase))
            return (true, "sender+body");

        return (false, "");
    }

    private async Task MoveS3Object(string sourceKey, string destKey, ILambdaContext ctx)
    {
        await _s3.CopyObjectAsync(EmailBucket, sourceKey, EmailBucket, destKey);
        await _s3.DeleteObjectAsync(EmailBucket, sourceKey);
        ctx.Logger.LogLine($"[extract] moved {sourceKey} → {destKey}");
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
