using System.Globalization;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>Port of 09-build-digest.js (per-broker daily) and 12-admin-digest.js (owner weekly).</summary>
public static class DigestBuilder
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Per-broker daily digest ("Resumen de ayer"). Returns null when there was no activity.</summary>
    public static string? BuildBrokerDaily(IReadOnlyList<Lead> yesterdayLeads, DateTimeOffset today)
    {
        if (yesterdayLeads.Count == 0) return null;

        var newCount = yesterdayLeads.Count(l => l.Status == LeadStatus.New);
        var qualifiedCount = yesterdayLeads.Count(l => l.Status is LeadStatus.Qualified or LeadStatus.Hot);
        var hotCount = yesterdayLeads.Count(l => l.Status == LeadStatus.Hot);

        var pending = yesterdayLeads
            .Where(l => !string.IsNullOrWhiteSpace(l.VisitAvailability) && !l.AlertSent)
            .Select(l =>
                $"• {l.Name ?? "Sin nombre"} — {l.Zone ?? "?"} · " +
                $"{(l.BudgetMax.HasValue ? "$" + l.BudgetMax.Value.ToString("N0", Mx) + " MXN" : "?")} · " +
                $"{l.VisitAvailability}")
            .ToList();

        var todayLabel = today.ToString("dddd d 'de' MMMM", Mx);

        var pendingSection = pending.Count > 0
            ? $"\n📞 *Pendientes de confirmar visita:*\n{string.Join('\n', pending)}"
            : "";

        return
            $"""
            ☀️ *Resumen de ayer — {todayLabel}*

            Leads nuevos: {newCount}
            Leads calificados: {qualifiedCount}
            Leads calientes 🔥: {hotCount}{pendingSection}

            💬 Escríbeme si quieres más detalle sobre algún lead o un consejo para cerrarlo.
            """;
    }

    public sealed record BrokerWeekStats(string Name, PlanTier Plan, int LeadsWeek, int HotWeek, int PropertiesActive);

    /// <summary>Owner-facing weekly digest across all brokers, with MRR estimate.</summary>
    public static string BuildAdminWeekly(IReadOnlyList<BrokerWeekStats> rows)
    {
        var totalLeads = rows.Sum(r => r.LeadsWeek);
        var totalHot = rows.Sum(r => r.HotWeek);
        var totalProperties = rows.Sum(r => r.PropertiesActive);

        var brokerLines = string.Join('\n', rows
            .OrderByDescending(r => r.LeadsWeek)
            .Select(r => $"• {r.Name} ({r.Plan.ToString().ToLowerInvariant()}): {r.LeadsWeek} leads, {r.HotWeek} hot, {r.PropertiesActive} props"));

        var monthlyRevenue = rows.Sum(r => r.Plan switch
        {
            PlanTier.Pro => 1200,
            PlanTier.Agencia => 2500,
            _ => 499,
        });

        return
            $"""
            📊 *BrokerAi — Resumen Semanal*

            👥 Brokers activos: {rows.Count}
            📥 Leads esta semana: {totalLeads}
            🔥 Hot leads: {totalHot}
            🏠 Propiedades activas: {totalProperties}
            💰 MRR estimado: ${monthlyRevenue.ToString("N0", Mx)} MXN

            *Por broker:*
            {(brokerLines.Length > 0 ? brokerLines : "(sin actividad)")}
            """;
    }
}
