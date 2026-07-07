using System.Net;
using System.Text.Json;
using Azure.Storage.Queues;
using BrokerAi.Core.Options;
using BrokerAi.Core.Webhook;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrokerAi.Functions;

/// <summary>
/// Workflow 1 entry point. GET handles Meta's webhook verification challenge.
/// POST parses the payload and enqueues real messages for async processing —
/// returns 200 immediately so Meta never times out and redelivers.
/// </summary>
public sealed class WhatsAppWebhookFunction(
    QueueServiceClient queueServiceClient,
    IOptions<MetaOptions> metaOptions,
    ILogger<WhatsAppWebhookFunction> logger)
{
    private const string QueueName = "incoming-messages";

    [Function("WhatsAppWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "whatsapp")] HttpRequestData req,
        CancellationToken ct)
    {
        if (req.Method == "GET")
            return await HandleVerificationAsync(req, ct);

        return await HandlePostAsync(req, ct);
    }

    private async Task<HttpResponseData> HandleVerificationAsync(HttpRequestData req, CancellationToken ct)
    {
        var query = QueryHelpers.ParseQuery(req.Url.Query);
        var mode = query.TryGetValue("hub.mode", out var m) ? m.ToString() : null;
        var token = query.TryGetValue("hub.verify_token", out var t) ? t.ToString() : null;
        var challenge = query.TryGetValue("hub.challenge", out var c) ? c.ToString() : null;

        if (mode == "subscribe" && token == metaOptions.Value.WebhookVerifyToken && challenge is not null)
        {
            var ok = req.CreateResponse(HttpStatusCode.OK);
            ok.Headers.Add("Content-Type", "text/plain");
            // WriteStringAsync: the ASP.NET Core host disallows synchronous IO.
            await ok.WriteStringAsync(challenge, ct);
            return ok;
        }

        logger.LogWarning("Webhook verification failed: mode={Mode}", mode);
        return req.CreateResponse(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseData> HandlePostAsync(HttpRequestData req, CancellationToken ct)
    {
        var body = await req.ReadAsStringAsync() ?? "";
        var result = WebhookParser.Parse(body);

        if (!result.Skip && result.Message is not null)
        {
            var queueClient = queueServiceClient.GetQueueClient(QueueName);
            await queueClient.CreateIfNotExistsAsync(cancellationToken: ct);
            var json = JsonSerializer.Serialize(result.Message);
            await queueClient.SendMessageAsync(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)), cancellationToken: ct);
        }
        else
        {
            logger.LogInformation("Webhook skipped: {Reason}", result.Reason);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "ok" }, ct);
        return response;
    }
}
