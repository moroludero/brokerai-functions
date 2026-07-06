using System.Globalization;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>Port of 08-build-alert.js — formats the 🔥 hot-lead WhatsApp alert for the broker.</summary>
public static class AlertBuilder
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>User-turn summary of the conversation, fed to the selling-arguments Claude call.</summary>
    public static string ConversationSummary(SessionContext context)
    {
        var lines = context.History
            .Where(t => t.Role == "user")
            .Select(t => $"- \"{t.Content}\"")
            .ToList();
        return lines.Count > 0 ? string.Join('\n', lines) : "(sin historial disponible)";
    }

    public static string Build(Lead lead, string coaching, string? qrShortCode = null, string? qrPropertyTitle = null)
    {
        var budget = lead.BudgetMin.HasValue && lead.BudgetMax.HasValue
            ? $"${lead.BudgetMin.Value.ToString("N0", Mx)}–${lead.BudgetMax.Value.ToString("N0", Mx)} MXN"
            : "No especificado";

        var visitLine = !string.IsNullOrWhiteSpace(lead.VisitAvailability)
            ? $"\n📅 *Disponibilidad:* {lead.VisitAvailability}"
            : "";

        var qrLine = qrShortCode is not null
            ? $"\n🏷️ *Escaneó el QR de:* {(qrPropertyTitle is not null ? $"{qrPropertyTitle} ({qrShortCode})" : qrShortCode)}"
            : "";

        var coachingText = string.IsNullOrWhiteSpace(coaching)
            ? "• Seguimiento personalizado recomendado"
            : coaching.Trim();

        return
            $"""
            🔥 *Nuevo lead calificado*

            👤 {lead.Name ?? "Sin nombre"}
            📱 {lead.Phone}
            💰 {budget}
            📍 {lead.Zone ?? "?"} · {lead.PropertyType ?? "?"}{visitLine}{qrLine}

            ¡Llámalos para confirmar la visita!
            👉 https://wa.me/{lead.Phone}

            🧠 *Recomendación basada en su conversación:*
            {coachingText}
            """;
    }
}
