using System.Net.Http.Json;
using BrokerAi.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrokerAi.Core.Services;

public interface IWhatsAppSender
{
    Task SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct = default);
    Task SendContactCardAsync(string phoneNumberId, string to, string name, string phone, CancellationToken ct = default);
    Task SendImageAsync(string phoneNumberId, string to, string imageUrl, string caption, CancellationToken ct = default);
    Task SendReactionAsync(string phoneNumberId, string to, string messageId, string emoji, CancellationToken ct = default);
    Task SendLocationRequestAsync(string phoneNumberId, string to, string bodyText, CancellationToken ct = default);
    Task SendLocationAsync(string phoneNumberId, string to, double latitude, double longitude, string? name, string? address, CancellationToken ct = default);
}

/// <summary>
/// All outbound WhatsApp sends. Host is graph.facebook.com — the old n8n export
/// pointed at graph.instagram.com, which was one of its fatal bugs. This class is
/// the single choke point so that can never regress.
/// </summary>
public sealed class WhatsAppSender(HttpClient http, IOptions<MetaOptions> options, ILogger<WhatsAppSender> logger) : IWhatsAppSender
{
    private readonly MetaOptions _meta = options.Value;

    public async Task SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "text",
            text = new { body },
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    public async Task SendContactCardAsync(string phoneNumberId, string to, string name, string phone, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "contacts",
            contacts = new[]
            {
                new
                {
                    name = new { formatted_name = name, first_name = name },
                    phones = new[] { new { phone = $"+{phone}", type = "CELL" } },
                },
            },
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    public async Task SendImageAsync(string phoneNumberId, string to, string imageUrl, string caption, CancellationToken ct = default)
    {
        // Meta rejects empty caption strings — omit the field entirely when blank.
        object image = string.IsNullOrWhiteSpace(caption)
            ? new { link = imageUrl }
            : new { link = imageUrl, caption };
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "image",
            image,
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    /// <summary>
    /// Reacts with an emoji to one of the user's messages — lightweight ack with
    /// zero chat clutter (used to acknowledge each photo in a forwarded batch
    /// instead of replying with N text messages).
    /// </summary>
    public async Task SendReactionAsync(string phoneNumberId, string to, string messageId, string emoji, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "reaction",
            reaction = new { message_id = messageId, emoji },
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    /// <summary>
    /// Interactive location request: shows a native "Enviar ubicación" button that
    /// opens WhatsApp's location-share screen — the official way to ask for a location.
    /// </summary>
    public async Task SendLocationRequestAsync(string phoneNumberId, string to, string bodyText, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "interactive",
            interactive = new
            {
                type = "location_request_message",
                body = new { text = bodyText },
                action = new { name = "send_location" },
            },
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    /// <summary>Sends a location pin (e.g. the property's exact spot when a visit is confirmed).</summary>
    public async Task SendLocationAsync(string phoneNumberId, string to, double latitude, double longitude, string? name, string? address, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "location",
            location = new
            {
                latitude,
                longitude,
                name,
                address,
            },
        };
        await PostAsync(phoneNumberId, payload, ct);
    }

    private async Task PostAsync(string phoneNumberId, object payload, CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{_meta.GraphApiVersion}/{phoneNumberId}/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new("Bearer", _meta.AccessToken);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("WhatsApp send failed ({Status}): {Error}", (int)response.StatusCode, error);
            response.EnsureSuccessStatusCode();
        }
    }
}
