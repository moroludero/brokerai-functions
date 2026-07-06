namespace BrokerAi.Core.Data.Entities;

/// <summary>
/// Idempotency guard: Meta redelivers webhooks on timeout/retry, and queue
/// retries can re-run a message. One row per processed WhatsApp message_id.
/// </summary>
public class ProcessedMessage
{
    public long Id { get; set; }
    public required string MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
