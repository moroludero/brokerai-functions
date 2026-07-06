using System.Text.Json.Serialization;

namespace BrokerAi.Core.Webhook;

/// <summary>Queue message DTO — the parsed WhatsApp message handed from the webhook to the processor.</summary>
public sealed class IncomingMessage
{
    [JsonPropertyName("from")]
    public required string From { get; set; }              // E.164 without '+'

    [JsonPropertyName("phone_number_id")]
    public required string PhoneNumberId { get; set; }

    [JsonPropertyName("message_id")]
    public required string MessageId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";             // text | image

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("media_id")]
    public string? MediaId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("is_voice")]
    public bool IsVoice { get; set; }
}
