using System.Text.Json;

namespace BrokerAi.Core.Webhook;

/// <summary>
/// Port of 00-parse-webhook.js — extracts the message from Meta's webhook payload.
/// Pure and static; unit-tested against fixture payloads.
/// </summary>
public static class WebhookParser
{
    public sealed record ParseResult(bool Skip, string? Reason, IncomingMessage? Message)
    {
        public static ParseResult Skipped(string reason) => new(true, reason, null);
        public static ParseResult Ok(IncomingMessage msg) => new(false, null, msg);
    }

    public static ParseResult Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return ParseResult.Skipped("invalid_json"); }

        using (doc)
        {
            if (!TryPath(doc.RootElement, out var value, "entry", 0, "changes", 0, "value"))
                return ParseResult.Skipped("no_value");

            // Status updates (delivery/read receipts) have no messages array
            if (!value.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                return ParseResult.Skipped("no_message");

            var message = messages[0];
            if (!value.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("phone_number_id", out var pni))
                return ParseResult.Skipped("no_metadata");

            var from = GetString(message, "from");
            var messageId = GetString(message, "id");
            var messageType = GetString(message, "type");
            if (from is null || messageId is null || messageType is null)
                return ParseResult.Skipped("malformed_message");

            long.TryParse(GetString(message, "timestamp"), out var timestamp);

            var msg = new IncomingMessage
            {
                From = from,
                PhoneNumberId = pni.GetString() ?? "",
                MessageId = messageId,
                Type = messageType,
                Timestamp = timestamp,
            };

            switch (messageType)
            {
                case "text":
                    // Old .js would throw if message.text was missing — guard it here.
                    if (!message.TryGetProperty("text", out var textEl) ||
                        !textEl.TryGetProperty("body", out var bodyEl))
                        return ParseResult.Skipped("text_without_body");
                    msg.Text = bodyEl.GetString()?.Trim();
                    return ParseResult.Ok(msg);

                case "image":
                    if (message.TryGetProperty("image", out var img))
                    {
                        msg.MediaId = GetString(img, "id");
                        msg.Text = img.TryGetProperty("caption", out var cap) ? cap.GetString()?.Trim() : null;
                    }
                    return ParseResult.Ok(msg);

                case "audio":
                case "voice":
                    msg.IsVoice = true;
                    return ParseResult.Ok(msg);

                default:
                    // Stickers, docs, location etc. — ignore
                    return ParseResult.Skipped($"unsupported_type:{messageType}");
            }
        }
    }

    /// <summary>Reply for unsupported voice notes (MVP is text-only).</summary>
    public const string VoiceAutoReply = "Por ahora solo puedo leer texto 😊 ¿Me escribes tu pregunta?";

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryPath(JsonElement el, out JsonElement result, params object[] path)
    {
        result = el;
        foreach (var seg in path)
        {
            if (seg is string s)
            {
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(s, out result))
                    return false;
            }
            else if (seg is int i)
            {
                if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() <= i)
                    return false;
                result = result[i];
            }
        }
        return true;
    }
}
