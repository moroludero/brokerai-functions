using System.Globalization;
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

    /// <summary>
    /// Pre-fill lead fields from a QR-scanned property. They chose this exact
    /// property, so zone/type/goal are known, and its price stands in for their
    /// budget — a QR lead is never interrogated about budget unless they decline
    /// the property (then everything here is cleared and normal flow restarts).
    /// </summary>
    public static void ApplyQrProperty(Lead lead, Property property)
    {
        lead.Zone ??= property.Zone;
        lead.PropertyType ??= property.Kind?.ToString().ToLowerInvariant();
        if (lead.Goal is null)
        {
            lead.Goal = property.ListingKind switch
            {
                ListingType.Renta => LeadGoal.Rentar,
                ListingType.Venta => LeadGoal.Comprar,
                _ => null, // "ambos": let them tell us
            };
        }
        var impliedBudget = property.ListingKind == ListingType.Renta ? property.RentPrice : property.Price;
        lead.BudgetMax ??= impliedBudget;
        lead.BudgetMin ??= impliedBudget;
    }

    /// <summary>
    /// Heuristic: the QR lead is passing on the scanned property ("no me parece",
    /// "no gracias", "muy caro", "otra opción") rather than giving availability.
    /// </summary>
    public static bool IsQrDecline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var norm = TextNormalizer.Normalize(text);
        if (norm == "no" || norm.StartsWith("no ") || norm.StartsWith("no,")) return true;
        return norm.Contains("no me") || norm.Contains("no gracias") || norm.Contains("no quiero") ||
               norm.Contains("muy car") || norm.Contains("otra opcion") || norm.Contains("otras opciones") ||
               norm.Contains("algo mas") || norm.Contains("algo diferente") || norm.Contains("no es lo que");
    }

    /// <summary>Clears the QR pre-fills so normal qualification restarts after a decline.</summary>
    public static void ClearQrPrefill(Lead lead)
    {
        lead.Zone = null;
        lead.PropertyType = null;
        lead.Goal = null;
        lead.BudgetMin = null;
        lead.BudgetMax = null;
        lead.VisitAvailability = null;
    }

    /// <summary>
    /// Property card shown to a lead who scanned the cartel QR — they chose this
    /// exact property, so the bot presents it before continuing qualification.
    /// </summary>
    public static string BuildPropertyCard(Property property)
    {
        var mx = CultureInfo.GetCultureInfo("es-MX");
        var typeLabel = property.Kind switch
        {
            PropertyKind.Casa => "🏡 Casa",
            PropertyKind.Depto => "🏢 Departamento",
            PropertyKind.Terreno => "🌿 Terreno",
            PropertyKind.Comercial => "🏪 Local Comercial",
            _ => "🏠 Propiedad",
        };
        var priceLine = property.ListingKind switch
        {
            ListingType.Renta => $"💰 ${(property.RentPrice ?? 0).ToString("N0", mx)} MXN/mes",
            ListingType.Ambos =>
                $"💰 Venta: ${(property.Price ?? 0).ToString("N0", mx)} MXN · Renta: ${(property.RentPrice ?? 0).ToString("N0", mx)} MXN/mes",
            _ => $"💰 ${(property.Price ?? 0).ToString("N0", mx)} MXN",
        };
        var rooms = property.Bedrooms.HasValue
            ? $"\n🛏 {property.Bedrooms} rec · 🚿 {property.Bathrooms} baños"
            : "";
        var description = string.IsNullOrWhiteSpace(property.Description)
            ? ""
            : $"\n\n{property.Description}";
        var video = string.IsNullOrWhiteSpace(property.VideoUrl)
            ? ""
            : $"\n🎥 Tour virtual: {property.VideoUrl}";

        return $"{typeLabel} — *{property.Title}*\n📍 {property.Zone}\n{priceLine}{rooms}{description}{video}";
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
