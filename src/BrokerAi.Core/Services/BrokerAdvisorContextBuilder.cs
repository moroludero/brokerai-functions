using System.Globalization;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>Port of 06-broker-advisor-context.js — assembles stats for the advisor Claude prompt.</summary>
public static class BrokerAdvisorContextBuilder
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public static string Build(IReadOnlyList<Lead> leadsLast7Days, IReadOnlyList<Property> properties)
    {
        var total = leadsLast7Days.Count;
        var qualified = leadsLast7Days.Count(l => l.Status is LeadStatus.Qualified or LeadStatus.Hot);
        var hot = leadsLast7Days.Count(l => l.Status == LeadStatus.Hot);
        var pendingVisit = leadsLast7Days.Count(l => !string.IsNullOrWhiteSpace(l.VisitAvailability) && !l.AlertSent);

        var activeProps = properties.Where(p => p.Active).ToList();
        var propsSummary = activeProps.Count > 0
            ? string.Join('\n', activeProps.Select(p =>
                $"• {p.ShortCode}: {p.Title} — {p.Zone}, ${(p.Price ?? 0).ToString("N0", Mx)} MXN, {p.Bedrooms}rec"))
            : "Sin propiedades activas aún.";

        var hotLeads = leadsLast7Days.Where(l => l.Score >= LeadScorer.HotThreshold).Take(5).ToList();
        var hotSummary = hotLeads.Count > 0
            ? string.Join('\n', hotLeads.Select(l =>
                $"• {l.Name ?? "Sin nombre"} — {l.Zone ?? "?"}, ${(l.BudgetMax ?? 0).ToString("N0", Mx)} MXN" +
                (!string.IsNullOrWhiteSpace(l.VisitAvailability) ? $", disponible: {l.VisitAvailability}" : "")))
            : "Sin leads calientes esta semana.";

        return
            $"""
            Broker's data this week:
            - Total leads: {total}
            - Qualified: {qualified}
            - Hot (score ≥70): {hot}
            - Pending visit confirmation: {pendingVisit}

            Active properties:
            {propsSummary}

            Recent hot leads:
            {hotSummary}
            """;
    }
}
