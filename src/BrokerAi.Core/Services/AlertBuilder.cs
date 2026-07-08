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

    /// <summary>
    /// Default lead opener when the AI one is unavailable — warm, personalized
    /// with whatever we know, single confirm-the-visit question.
    /// </summary>
    public static string FallbackOpener(Lead lead, string brokerName, string? qrPropertyTitle)
    {
        var hola = string.IsNullOrWhiteSpace(lead.Name) ? "¡Hola!" : $"¡Hola, {lead.Name.Trim().Split(' ')[0]}!";
        var propRef = qrPropertyTitle is not null ? $" sobre {qrPropertyTitle}" : " sobre la propiedad que te interesó";
        var visitRef = !string.IsNullOrWhiteSpace(lead.VisitAvailability)
            ? $" ¿Te confirmo la visita para {lead.VisitAvailability}?"
            : " ¿Qué día te gustaría visitarla?";
        return $"{hola} Soy {brokerName}, me escribiste{propRef}. Con gusto te atiendo 😊{visitRef}";
    }

    public static string Build(Lead lead, string coaching, string? openerForLead = null, string brokerName = "", string? qrShortCode = null, string? qrPropertyTitle = null)
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

        // Dialable number: strip the legacy Mexican '1' WhatsApp adds (521... → 52...)
        var phone = PhoneNumbers.ToDialableMx(lead.Phone);

        // Deep link that opens the lead's chat with a personalized, ready-to-send
        // message the broker can review, edit, or replace before sending.
        var opener = string.IsNullOrWhiteSpace(openerForLead)
            ? FallbackOpener(lead, brokerName, qrPropertyTitle)
            : openerForLead;
        var openerLink = $"https://wa.me/{phone}?text={Uri.EscapeDataString(opener)}";

        return
            $"""
            🔥 *Nuevo lead calificado*

            👤 {lead.Name ?? "Sin nombre"}
            📱 {phone}
            💰 {budget}
            📍 {lead.Zone ?? "?"} · {lead.PropertyType ?? "?"}{visitLine}{qrLine}

            ✍️ *Toca para escribirle — el mensaje ya va listo (revísalo y envía):*
            👉 {openerLink}

            🧠 *Recomendación basada en su conversación:*
            {coachingText}
            """;
    }
}
