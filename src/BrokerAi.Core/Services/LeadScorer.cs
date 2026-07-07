using BrokerAi.Core.Data.Entities;

namespace BrokerAi.Core.Services;

/// <summary>Port of 03-lead-score.js — 0–100 additive score; hot at ≥ 70 with alert dedup.</summary>
public static class LeadScorer
{
    public const int HotThreshold = 70;

    public sealed record Result(int Score, bool IsHot);

    public static Result Score(Lead lead)
    {
        var score = 0;

        // Budget signals
        if (lead.BudgetMax >= 3_000_000) score += 30;
        else if (lead.BudgetMax >= 1_000_000) score += 15;

        // Location signal
        if (!string.IsNullOrWhiteSpace(lead.Zone)) score += 20;

        // Property type signal
        if (!string.IsNullOrWhiteSpace(lead.PropertyType)) score += 20;

        // Full profile complete
        if (!string.IsNullOrWhiteSpace(lead.Name) && lead.BudgetMin.HasValue &&
            !string.IsNullOrWhiteSpace(lead.Zone) && !string.IsNullOrWhiteSpace(lead.PropertyType))
            score += 10;

        // Visit intent — strongest buying signal
        if (!string.IsNullOrWhiteSpace(lead.VisitAvailability)) score += 20;

        return new Result(score, score >= HotThreshold && !lead.AlertSent);
    }

    /// <summary>
    /// A lead who scanned a specific property's cartel QR and gave visit
    /// availability is always alert-worthy: the additive scale is sale-oriented
    /// (points for $1M+ budgets), so rental QR leads — implied budget = monthly
    /// rent — could mathematically never reach the hot threshold.
    /// </summary>
    public static bool IsQrVisitHot(Lead lead, string? qrShortCode) =>
        qrShortCode is not null &&
        !string.IsNullOrWhiteSpace(lead.VisitAvailability) &&
        !lead.AlertSent;
}
