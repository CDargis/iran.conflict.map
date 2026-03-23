using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IranConflictMap.Cleanup;

public class Function
{
    private readonly IAmazonSQS _sqs;

    private static readonly string ReviewQueueUrl = Env("REVIEW_QUEUE_URL", "");

    public Function()
    {
        _sqs = new AmazonSQSClient();
    }

    public Function(IAmazonSQS sqs)
    {
        _sqs = sqs;
    }

    // ── SQS trigger ───────────────────────────────────────────────────────────
    // Message body: { "source_url": "https://...", "current_run_id": "2026-03-23T18:00:00Z" }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (SQSEvent.SQSMessage record in sqsEvent.Records)
        {
            await ProcessMessage(record.Body, context);
        }
    }

    private async Task ProcessMessage(string messageBody, ILambdaContext context)
    {
        CleanupMessage msg = JsonSerializer.Deserialize<CleanupMessage>(messageBody)
            ?? throw new Exception($"Could not deserialize cleanup message: {messageBody[..Math.Min(messageBody.Length, 200)]}");

        context.Logger.LogLine($"[cleanup] starting: source_url={msg.SourceUrl} current_run_id={msg.CurrentRunId}");

        int totalDeleted = 0;
        int passes       = 0;

        while (true)
        {
            passes++;

            ReceiveMessageResponse response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl            = ReviewQueueUrl,
                MaxNumberOfMessages = 10,
                VisibilityTimeout   = 30   // enough time to process the batch; keepers are restored to 0 immediately
            });

            if (response.Messages.Count == 0)
            {
                context.Logger.LogLine($"[cleanup] queue empty after {passes} pass(es) — done");
                break;
            }

            int deletedThisPass = 0;

            foreach (Message message in response.Messages)
            {
                string? messageSourceUrl = null;
                string? messageSyncId    = null;

                try
                {
                    JsonDocument doc = JsonDocument.Parse(message.Body);
                    if (doc.RootElement.TryGetProperty("source_url", out JsonElement srcEl) && srcEl.ValueKind == JsonValueKind.String)
                    {
                        messageSourceUrl = srcEl.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("sync_id", out JsonElement syncEl) && syncEl.ValueKind == JsonValueKind.String)
                    {
                        messageSyncId = syncEl.GetString();
                    }
                }
                catch (JsonException ex)
                {
                    context.Logger.LogLine($"[cleanup] could not parse review message body: {ex.Message} — restoring visibility");
                    await RestoreVisibility(message.ReceiptHandle, context);
                    continue;
                }

                // Stale = same URL, wrong (older) run_id
                bool isStale = messageSourceUrl == msg.SourceUrl && messageSyncId != msg.CurrentRunId;

                if (isStale)
                {
                    await _sqs.DeleteMessageAsync(new DeleteMessageRequest
                    {
                        QueueUrl      = ReviewQueueUrl,
                        ReceiptHandle = message.ReceiptHandle
                    });
                    deletedThisPass++;
                    context.Logger.LogLine($"[cleanup] discarded stale review item: source_url={messageSourceUrl} sync_id={messageSyncId}");
                }
                else
                {
                    // Not our target URL, or already the current run — put it back immediately
                    await RestoreVisibility(message.ReceiptHandle, context);
                }
            }

            totalDeleted += deletedThisPass;
            context.Logger.LogLine($"[cleanup] pass {passes}: deleted={deletedThisPass} running_total={totalDeleted}");

            // If we saw messages but deleted none, all remaining messages are keepers — stop
            if (deletedThisPass == 0)
            {
                context.Logger.LogLine($"[cleanup] no deletions this pass — done");
                break;
            }
        }

        context.Logger.LogLine($"[cleanup] complete: total_deleted={totalDeleted} passes={passes}");
    }

    private async Task RestoreVisibility(string receiptHandle, ILambdaContext context)
    {
        try
        {
            await _sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl          = ReviewQueueUrl,
                ReceiptHandle     = receiptHandle,
                VisibilityTimeout = 0
            });
        }
        catch (Exception ex)
        {
            // Non-fatal — message will become visible again on its own after the 30s timeout
            context.Logger.LogLine($"[cleanup] warning: could not restore message visibility: {ex.Message}");
        }
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

// ── Message model ──────────────────────────────────────────────────────────────

public record CleanupMessage(
    [property: JsonPropertyName("source_url")]      string SourceUrl,
    [property: JsonPropertyName("current_run_id")]  string CurrentRunId
);
