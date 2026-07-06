using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Options;
using Microsoft.Extensions.Options;

namespace BrokerAi.Core.Services;

public interface IFacebookService
{
    Task<PostResult> PostPropertyAsync(Property property, CancellationToken ct = default);
    Task<AdResult> CreateAdAsync(Property property, string fbPostId, int budgetMxn, int durationDays, CancellationToken ct = default);
}

public sealed record PostResult(string FbPostId, string PostUrl);

public sealed record AdResult(string CampaignId, string AdSetId, string CreativeId, string AdId, int BilledMxn);

/// <summary>
/// Ports 10-facebook-post.js (organic post) and 11-facebook-ad.js (4-step paid
/// ad chain: Campaign → Ad Set → Ad Creative → Ad). FIX vs old design: BilledMxn
/// is computed here from AdMarkupPercent — the old node referenced prop.billed_mxn
/// without ever computing it, so the confirmation message read "$undefined MXN".
/// </summary>
public sealed class FacebookService(HttpClient http, IOptions<FacebookOptions> options, IOptions<AppOptions> appOptions) : IFacebookService
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");
    private readonly FacebookOptions _fb = options.Value;

    private static readonly Dictionary<string, string> TypeLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["casa"] = "🏡 Casa",
        ["depto"] = "🏢 Departamento",
        ["terreno"] = "🌿 Terreno",
        ["comercial"] = "🏪 Local Comercial",
    };

    public async Task<PostResult> PostPropertyAsync(Property property, CancellationToken ct = default)
    {
        var typeLabel = property.Kind is not null && TypeLabel.TryGetValue(property.Kind.ToString()!, out var label)
            ? label : property.Kind?.ToString() ?? "";
        var price = property.Price.HasValue
            ? $"${property.Price.Value.ToString("N0", Mx)} MXN"
            : "Precio a consultar";
        var rooms = property.Bedrooms.HasValue
            ? $"🛏 {property.Bedrooms} rec · 🚿 {property.Bathrooms} baños\n"
            : "";
        var videoLine = !string.IsNullOrWhiteSpace(property.VideoUrl)
            ? $"\n🎥 Tour virtual: {property.VideoUrl}"
            : "";
        var waLink = ShortCodeGenerator.QrLink(appOptions.Value.SharedWhatsAppNumber, property.ShortCode!);

        var caption =
            $"""
            {typeLabel} en {property.Zone}
            💰 {price}
            {rooms}
            {property.Description}{videoLine}

            📲 ¿Te interesa? Escríbenos por WhatsApp:
            {waLink}
            """;

        var hasImage = !string.IsNullOrWhiteSpace(property.ImageUrl);
        var endpoint = hasImage ? "photos" : "feed";
        object body = hasImage
            ? new { url = property.ImageUrl, caption, published = true, access_token = _fb.PageAccessToken }
            : new { message = caption, published = true, access_token = _fb.PageAccessToken };

        var response = await http.PostAsJsonAsync(
            $"https://graph.facebook.com/v21.0/{_fb.PageId}/{endpoint}", body, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var postId = json.GetProperty("id").GetString()!;

        return new PostResult(postId, $"https://www.facebook.com/{_fb.PageId}/posts/{postId}");
    }

    public async Task<AdResult> CreateAdAsync(Property property, string fbPostId, int budgetMxn, int durationDays, CancellationToken ct = default)
    {
        var budgetCents = budgetMxn * 100;
        var now = DateTimeOffset.UtcNow;
        var endTime = now.AddDays(durationDays);
        var adAccount = $"https://graph.facebook.com/v21.0/{_fb.AdAccountId}";

        // 1. Campaign
        var campaignBody = new
        {
            name = $"BrokerAi - {property.ShortCode} - {now:yyyy-MM-dd}",
            objective = "OUTCOME_ENGAGEMENT",
            status = "ACTIVE",
            special_ad_categories = Array.Empty<string>(),
            access_token = _fb.PageAccessToken,
        };
        var campaignId = await PostAndGetId($"{adAccount}/campaigns", campaignBody, ct);

        // 2. Ad Set (targeting: Quintana Roo, covers Cancún/Playa/Tulum/Puerto Morelos)
        var adSetBody = new
        {
            name = $"AdSet - {property.ShortCode}",
            campaign_id = campaignId,
            lifetime_budget = budgetCents,
            start_time = now.ToUnixTimeSeconds(),
            end_time = endTime.ToUnixTimeSeconds(),
            billing_event = "IMPRESSIONS",
            optimization_goal = "REACH",
            targeting = new
            {
                geo_locations = new { regions = new[] { new { key = "3836", name = "Quintana Roo", country = "MX" } } },
                age_min = 28,
                age_max = 60,
            },
            status = "ACTIVE",
            access_token = _fb.PageAccessToken,
        };
        var adSetId = await PostAndGetId($"{adAccount}/adsets", adSetBody, ct);

        // 3. Ad Creative (from the organic post)
        var creativeBody = new
        {
            name = $"Creative - {property.ShortCode}",
            object_story_id = $"{_fb.PageId}_{fbPostId}",
            access_token = _fb.PageAccessToken,
        };
        var creativeId = await PostAndGetId($"{adAccount}/adcreatives", creativeBody, ct);

        // 4. Ad
        var adBody = new
        {
            name = $"Ad - {property.ShortCode}",
            adset_id = adSetId,
            creative = new { creative_id = creativeId },
            status = "ACTIVE",
            access_token = _fb.PageAccessToken,
        };
        var adId = await PostAndGetId($"{adAccount}/ads", adBody, ct);

        // FIX: billed_mxn is computed here (old design referenced it without computing it).
        var billedMxn = (int)Math.Round(budgetMxn * (1 + _fb.AdMarkupPercent / 100.0));

        return new AdResult(campaignId, adSetId, creativeId, adId, billedMxn);
    }

    public static string BuildAdConfirmation(string shortCode, int durationDays, int billedMxn, string postUrl) =>
        $"✅ Anuncio activo para {shortCode}\n📅 Duración: {durationDays} días\n🎯 Cancún, Playa del Carmen, Tulum\n" +
        $"💰 Te facturo ${billedMxn.ToString("N0", Mx)} MXN esta semana\n🔗 {postUrl}";

    private async Task<string> PostAndGetId(string url, object body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(url, body, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.GetProperty("id").GetString()!;
    }
}
