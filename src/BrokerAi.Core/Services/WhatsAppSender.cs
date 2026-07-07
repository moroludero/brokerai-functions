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
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "image",
            image = new { link = imageUrl, caption },
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
