using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>
/// Pure lead-qualification step logic (Workflow 3). Merges extracted data into
/// the lead every turn — a question is never asked once its answer is known —
/// then asks for the next missing field: goal → budget → zone → type → visit.
/// QR leads skip zone/type (pre-filled from the scanned property).
/// </summary>
public static class LeadQualificationEngine
{
    public sealed record Output(string Reply, string NextStep, bool ReadyForScoring, LeadStatus NewStatus);

    public const string OffTopicReply =
        "Solo puedo ayudarte con temas de bienes raíces en Cancún y la Riviera Maya 🏠 " +
        "¿Buscas comprar o rentar una propiedad?";

    /// <summary>Merge non-null extracted fields into the lead. Never overwrites a known value with null.</summary>
    public static void MergeExtraction(Lead lead, LeadExtraction extraction)
    {
        var e = extraction.Extracted;
        lead.Name ??= e.Name;
        lead.BudgetMin ??= e.BudgetMin;
        lead.BudgetMax ??= e.BudgetMax;
        // A single stated budget lands in BudgetMax; mirror it into BudgetMin if still empty
        if (lead.BudgetMax.HasValue && !lead.BudgetMin.HasValue) lead.BudgetMin = lead.BudgetMax;
        lead.Zone ??= NormalizeZone(e.Zone);
        lead.PropertyType ??= NormalizePropertyType(e.PropertyType);
        lead.VisitAvailability ??= e.VisitAvailability;
        if (lead.Goal is null && extraction.LeadGoal is not null)
        {
            lead.Goal = extraction.LeadGoal.Equals("rentar", StringComparison.OrdinalIgnoreCase)
                ? LeadGoal.Rentar
                : LeadGoal.Comprar;
        }
        if (extraction.Language is "en" or "es") lead.Language = extraction.Language;
    }

    /// <summary>Pre-fill lead fields from a QR-scanned property (zone + type already chosen).</summary>
    public static void ApplyQrProperty(Lead lead, Property property)
    {
        lead.Zone ??= property.Zone;
        lead.PropertyType ??= property.Kind?.ToString().ToLowerInvariant();
        if (lead.Goal is null && property.ListingKind == ListingType.Renta) lead.Goal = LeadGoal.Rentar;
    }

    /// <summary>Decide the next question/step after extraction has been merged.</summary>
    public static Output Advance(Lead lead, string currentStep, string brokerName, bool isFirstMessage)
    {
        // Ask for the next missing field, in qualification order.
        if (lead.Goal is null)
        {
            var greeting = isFirstMessage
                ? $"¡Hola! 😊 Soy el asistente de {brokerName}. "
                : "";
            return new($"{greeting}¿Buscas *comprar* o *rentar* una propiedad?",
                LeadSteps.ListingTypeStep, false, LeadStatus.Qualifying);
        }

        if (!lead.BudgetMax.HasValue)
        {
            return new("¿Cuál es tu presupuesto aproximado en MXN? 💰",
                LeadSteps.Budget, false, LeadStatus.Qualifying);
        }

        if (string.IsNullOrWhiteSpace(lead.Zone))
        {
            return new("¿En qué zona te interesa?\n(Cancún Centro / Zona Hotelera / Playa del Carmen / Tulum / Puerto Morelos) 📍",
                LeadSteps.Zone, false, LeadStatus.Qualifying);
        }

        if (string.IsNullOrWhiteSpace(lead.PropertyType))
        {
            return new("¿Qué tipo de propiedad buscas? (casa / depto / terreno / comercial) 🏠",
                LeadSteps.PropertyType, false, LeadStatus.Qualifying);
        }

        if (string.IsNullOrWhiteSpace(lead.VisitAvailability))
        {
            return new("¡Perfecto! ¿Qué día y hora te acomoda para una visita? (ej: jueves por la tarde) 📅",
                LeadSteps.VisitAvailability, false, LeadStatus.Qualifying);
        }

        // Everything collected — score and (maybe) alert; caller sends the recommendation reply.
        return new("", LeadSteps.Qualified, true, LeadStatus.Qualified);
    }

    private static string? NormalizeZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone)) return null;
        var norm = TextNormalizer.Normalize(zone);
        return Zones.All.FirstOrDefault(z => TextNormalizer.Normalize(z) == norm) ?? zone.Trim();
    }

    private static string? NormalizePropertyType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var norm = TextNormalizer.Normalize(type);
        return norm switch
        {
            "casa" or "depto" or "terreno" or "comercial" => norm,
            "departamento" => "depto",
            _ => null,
        };
    }
}
