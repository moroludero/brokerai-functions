using System.Reflection;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using BrokerAi.Core.Domain;
using BrokerAi.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrokerAi.Core.Services;

/// <summary>Abstraction over Claude calls so state machines are unit-testable.</summary>
public interface IClaudeGateway
{
    Task<LeadExtraction> ClassifyAsync(string message, CancellationToken ct = default);
    Task<string> AdviseAsync(string brokerDataContext, string brokerMessage, CancellationToken ct = default);
    Task<string> SellingArgumentsAsync(string leadProfile, string conversationSummary, CancellationToken ct = default);
}

/// <summary>
/// Wraps the official Anthropic SDK. Static prompt text lives in a cached system
/// block (CacheControlEphemeral); all per-request data goes in the user block —
/// never interpolated into system, so the cache prefix stays byte-identical.
/// Note: Haiku 4.5's minimum cacheable prefix is 4096 tokens; current prompts are
/// smaller, so cache_read reports 0 until they grow. Structure is still correct.
/// </summary>
public sealed class ClaudeService(IOptions<AnthropicOptions> options, ILogger<ClaudeService> logger) : IClaudeGateway
{
    private readonly AnthropicClient _client = new() { ApiKey = options.Value.ApiKey };
    private readonly string _model = options.Value.Model;

    private static readonly string ClassificationPrompt = LoadPrompt("classification.txt");
    private static readonly string AdvisorPrompt = LoadPrompt("advisor.txt");
    private static readonly string SellingArgumentsPrompt = LoadPrompt("selling-arguments.txt");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<LeadExtraction> ClassifyAsync(string message, CancellationToken ct = default)
    {
        var text = await CreateAsync(ClassificationPrompt, $"Message: \"{message}\"", maxTokens: 300, ct);
        try
        {
            var parsed = JsonSerializer.Deserialize<LeadExtraction>(ExtractJson(text), JsonOpts);
            if (parsed is not null) return parsed;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Classification returned unparseable JSON: {Text}", text);
        }
        // Fail open as a vague on-topic message so the conversation continues.
        return new LeadExtraction { Intent = "vague", Language = "es" };
    }

    public Task<string> AdviseAsync(string brokerDataContext, string brokerMessage, CancellationToken ct = default) =>
        CreateAsync(AdvisorPrompt, $"{brokerDataContext}\n\nBroker's message: \"{brokerMessage}\"", maxTokens: 500, ct);

    public Task<string> SellingArgumentsAsync(string leadProfile, string conversationSummary, CancellationToken ct = default) =>
        CreateAsync(SellingArgumentsPrompt,
            $"Lead profile:\n{leadProfile}\n\nWhat the lead said during the conversation:\n{conversationSummary}",
            maxTokens: 500, ct);

    private async Task<string> CreateAsync(string systemPrompt, string userContent, int maxTokens, CancellationToken ct)
    {
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new() { Role = Role.User, Content = userContent }],
        }, cancellationToken: ct);

        logger.LogInformation(
            "Claude call: in={Input} cache_read={CacheRead} cache_write={CacheWrite} out={Output}",
            response.Usage.InputTokens, response.Usage.CacheReadInputTokens,
            response.Usage.CacheCreationInputTokens, response.Usage.OutputTokens);

        var text = string.Concat(response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => t.Text));
        return text.Trim();
    }

    /// <summary>Strips markdown fences the model occasionally wraps around JSON.</summary>
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string LoadPrompt(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded prompt not found: {fileName}");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
