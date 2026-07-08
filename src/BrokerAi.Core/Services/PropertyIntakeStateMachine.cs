using System.Globalization;
using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>
/// Broker property-intake state machine (port of 05-broker-intake.js, evolved).
/// Pure: state in, state + reply out.
/// Chain: listing_type → type → zone → price → (rent_price) → bedrooms →
///        bathrooms → photo(loop) → description → video → confirm → done.
///
/// Correction UX follows established conversational-form patterns
/// (summary+confirm before commit, back navigation, targeted slot correction):
/// - "atrás" at any step returns to the previous question
/// - "cancelar" aborts the whole intake
/// - a summary screen before saving accepts targeted fixes ("baños 1",
///   "precio 2500000", "zona tulum", ...) and only persists on "confirmar"
/// </summary>
public static class PropertyIntakeStateMachine
{
    /// <param name="ReactWithEmoji">When set, acknowledge the user's message with this emoji reaction instead of (or besides) a text reply — keeps forwarded photo batches from triggering N text responses.</param>
    public sealed record Result(BrokerIntakeState NextState, string? Reply, bool Done, bool Error, bool Cancelled = false, string? ReactWithEmoji = null);

    private static readonly string[] ValidTypes = ["casa", "depto", "departamento", "terreno", "comercial"];

    private static readonly (string Key, string Zone)[] ZoneMap =
    [
        ("cancun", Zones.CancunCentro),
        ("hotelera", Zones.ZonaHotelera),
        ("playa", Zones.PlayaDelCarmen),
        ("tulum", Zones.Tulum),
        ("morelos", Zones.PuertoMorelos),
    ];

    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public static Result Advance(BrokerIntakeState state, string? text, string? mediaId)
    {
        var input = (text ?? "").Trim();
        var norm = TextNormalizer.Normalize(input);
        var data = state.Data;
        var step = state.Step;

        // Global commands, available at every step.
        if (norm == "cancelar" || norm == "cancela")
            return new(state, "❌ Alta cancelada. Escribe *agregar* cuando quieras empezar de nuevo.", false, false, Cancelled: true);

        if (norm == "atras" && string.IsNullOrEmpty(mediaId))
        {
            var prev = PreviousStep(step, data);
            if (prev is null)
                return Stay(state, data, step, "Estás en la primera pregunta. " + QuestionFor(step, data), error: true);
            return Stay(state, data, prev, "↩️ " + QuestionFor(prev, data));
        }

        string? reply = null;
        var error = false;

        switch (step)
        {
            case IntakeSteps.ListingTypeStep:
                // Match on stems so conjugations work: "que se rente", "venderla",
                // "para arrendar". Both stems present (e.g. "venta y renta") = ambos.
                var wantsVenta = norm.Contains("venta") || norm.Contains("vend") || norm.Contains("compra");
                var wantsRenta = norm.Contains("rent") || norm.Contains("alquil") || norm.Contains("arrend");
                var wantsAmbos = norm.Contains("ambos") || norm.Contains("ambas") ||
                                 norm.Contains("los dos") || norm.Contains("las dos") ||
                                 (wantsVenta && wantsRenta);
                if (wantsAmbos) data.ListingType = "ambos";
                else if (wantsVenta) data.ListingType = "venta";
                else if (wantsRenta) data.ListingType = "renta";
                else { reply = QuestionFor(step, data); error = true; break; }
                break;

            case IntakeSteps.Type:
                var match = ValidTypes.FirstOrDefault(t => norm.Contains(t));
                if (match is null) { reply = QuestionFor(step, data); error = true; break; }
                data.Type = match == "departamento" ? "depto" : match;
                break;

            case IntakeSteps.Zone:
                if (!TrySetZone(data, norm)) { reply = "No reconocí la zona. " + QuestionFor(step, data); error = true; }
                break;

            case IntakeSteps.Price:
                var priceNum = ParseDigits(input);
                // Renta uses the rent minimum here — the sale minimum (50k) would
                // reject every normal monthly rent and block the renta path entirely.
                var minPrice = data.ListingType == "renta" ? 1_000 : 50_000;
                if (priceNum is null || priceNum < minPrice) { reply = QuestionFor(step, data); error = true; break; }
                if (data.ListingType == "renta") { data.Price = null; data.RentPrice = priceNum; }
                else data.Price = priceNum;
                break;

            case IntakeSteps.RentPrice:
                var rentNum = ParseDigits(input);
                if (rentNum is null || rentNum < 1_000) { reply = QuestionFor(step, data); error = true; break; }
                data.RentPrice = rentNum;
                break;

            case IntakeSteps.Bedrooms:
                if (!int.TryParse(input, out var bedrooms) || bedrooms < 0 || bedrooms > 20)
                { reply = QuestionFor(step, data); error = true; break; }
                data.Bedrooms = bedrooms;
                break;

            case IntakeSteps.Bathrooms:
                if (!int.TryParse(input, out var bathrooms) || bathrooms < 1 || bathrooms > 20)
                { reply = QuestionFor(step, data); error = true; break; }
                data.Bathrooms = bathrooms;
                break;

            case IntakeSteps.Photo:
                // Loop: properties usually have several photos, often forwarded as a
                // batch (each arrives as a separate webhook — Cloud API has no album
                // grouping). Acknowledge each with a silent 📸 reaction instead of a
                // text reply; the only text comes on "listo" with the total count.
                if (!string.IsNullOrEmpty(mediaId))
                {
                    data.MediaIds.Add(mediaId);
                    return new(new BrokerIntakeState { Step = step, Data = data }, null,
                        Done: false, Error: false, ReactWithEmoji: "📸");
                }
                if (data.MediaIds.Count > 0 &&
                    (norm.Contains("listo") || norm.Contains("ya") || norm.Contains("es todo") || norm.Contains("continuar")))
                {
                    data.PhotosDone = true;
                    var afterPhotos = NextMissingAfter(step, data);
                    return Stay(state, data, afterPhotos,
                        $"✅ {data.MediaIds.Count} foto{(data.MediaIds.Count == 1 ? "" : "s")} recibida{(data.MediaIds.Count == 1 ? "" : "s")} 📸\n\n" +
                        QuestionFor(afterPhotos, data));
                }
                if (norm.Contains("sin foto") || norm.Contains("no foto"))
                { data.PhotosDone = true; break; }
                reply = data.MediaIds.Count > 0
                    ? "Manda otra foto o escribe *listo* para continuar"
                    : "Mándame una foto, o escribe *sin foto* para continuar";
                error = true;
                break;

            case IntakeSteps.Description:
                if (input.Length < 10) { reply = QuestionFor(step, data); error = true; break; }
                data.Description = input;
                break;

            case IntakeSteps.Video:
                if (norm.Contains("sin video") || norm.Contains("no video") || norm.Contains("no tengo"))
                { data.VideoUrl = null; data.VideoDone = true; break; }
                if (input.StartsWith("http")) { data.VideoUrl = input; data.VideoDone = true; break; }
                reply = QuestionFor(step, data);
                error = true;
                break;

            case IntakeSteps.Confirm:
                return HandleConfirm(state, data, norm, input);
        }

        if (error)
            return Stay(state, data, step, reply, error: true);

        // Advance to the first still-missing field AFTER the current step. Keeps
        // the flow linear, and after a correction jumps back (e.g. "fotos" from
        // the summary) it skips fields already answered and returns to Confirm.
        var next = NextMissingAfter(step, data);
        return Stay(state, data, next, QuestionFor(next, data));
    }

    // ---------------------------------------------------------------- confirm screen

    private static Result HandleConfirm(BrokerIntakeState state, IntakeData data, string norm, string input)
    {
        if (norm is "confirmar" or "confirmo" or "si" or "guardar" or "ok")
            return new(new BrokerIntakeState { Step = IntakeSteps.Done, Data = data }, null, Done: true, Error: false);

        // Targeted corrections: "<campo> <valor>"
        var firstSpace = input.IndexOf(' ');
        var field = TextNormalizer.Normalize(firstSpace > 0 ? input[..firstSpace] : input);
        var value = firstSpace > 0 ? input[(firstSpace + 1)..].Trim() : "";
        var normValue = TextNormalizer.Normalize(value);

        switch (field)
        {
            case "banos" or "bano":
                if (int.TryParse(value, out var b) && b is >= 1 and <= 20) { data.Bathrooms = b; return Summary(state, data, "✏️ Baños actualizados."); }
                return Stay(state, data, IntakeSteps.Confirm, "Ejemplo: *baños 1*", error: true);
            case "recamaras" or "recamara":
                if (int.TryParse(value, out var r) && r is >= 0 and <= 20) { data.Bedrooms = r; return Summary(state, data, "✏️ Recámaras actualizadas."); }
                return Stay(state, data, IntakeSteps.Confirm, "Ejemplo: *recamaras 2*", error: true);
            case "precio":
                var p = ParseDigits(value);
                if (p >= 50_000) { data.Price = p; return Summary(state, data, "✏️ Precio actualizado."); }
                return Stay(state, data, IntakeSteps.Confirm, "Ejemplo: *precio 2500000*", error: true);
            case "renta":
                var rp = ParseDigits(value);
                if (rp >= 1_000) { data.RentPrice = rp; return Summary(state, data, "✏️ Renta actualizada."); }
                return Stay(state, data, IntakeSteps.Confirm, "Ejemplo: *renta 15000*", error: true);
            case "zona":
                if (TrySetZone(data, normValue)) return Summary(state, data, "✏️ Zona actualizada.");
                return Stay(state, data, IntakeSteps.Confirm, "Zonas: Cancún Centro, Zona Hotelera, Playa del Carmen, Tulum, Puerto Morelos", error: true);
            case "tipo":
                var t = ValidTypes.FirstOrDefault(v => normValue.Contains(v));
                if (t is not null) { data.Type = t == "departamento" ? "depto" : t; return Summary(state, data, "✏️ Tipo actualizado."); }
                return Stay(state, data, IntakeSteps.Confirm, "Tipos: casa, depto, terreno, comercial", error: true);
            case "descripcion":
                if (value.Length >= 10) { data.Description = value; return Summary(state, data, "✏️ Descripción actualizada."); }
                return Stay(state, data, IntakeSteps.Confirm, "Escribe: *descripcion* seguida del texto nuevo (mínimo 10 caracteres)", error: true);
            case "video":
                if (value.StartsWith("http")) { data.VideoUrl = value; return Summary(state, data, "✏️ Video actualizado."); }
                if (normValue.Contains("sin")) { data.VideoUrl = null; return Summary(state, data, "✏️ Video quitado."); }
                return Stay(state, data, IntakeSteps.Confirm, "Ejemplo: *video https://...* o *video sin*", error: true);
            case "fotos" or "foto":
                data.PhotosDone = false;
                return Stay(state, data, IntakeSteps.Photo,
                    "📸 Manda más fotos (se suman a las que ya tienes) y escribe *listo* para volver al resumen");
            default:
                return Stay(state, data, IntakeSteps.Confirm,
                    "No entendí. Responde *confirmar* para guardar, *cancelar* para descartar, " +
                    "o corrige un campo, ej: *baños 1*, *precio 2500000*, *zona tulum*", error: true);
        }
    }

    private static Result Summary(BrokerIntakeState state, IntakeData data, string? prefix = null) =>
        Stay(state, data, IntakeSteps.Confirm, (prefix is null ? "" : prefix + "\n\n") + BuildSummary(data));

    /// <summary>Recap of everything captured + instructions — nothing saves without "confirmar".</summary>
    public static string BuildSummary(IntakeData data)
    {
        var priceLine = data.ListingType switch
        {
            "renta" => $"💰 Renta: ${(data.RentPrice ?? 0).ToString("N0", Mx)} MXN/mes",
            "ambos" => $"💰 Venta: ${(data.Price ?? 0).ToString("N0", Mx)} MXN · Renta: ${(data.RentPrice ?? 0).ToString("N0", Mx)} MXN/mes",
            _ => $"💰 Precio: ${(data.Price ?? 0).ToString("N0", Mx)} MXN",
        };
        var video = data.VideoUrl is null ? "sin video" : data.VideoUrl;
        return
            $"""
            📋 *Revisa los datos antes de guardar:*

            🏷 Operación: {data.ListingType}
            🏠 Tipo: {data.Type}
            📍 Zona: {data.Zone}
            {priceLine}
            🛏 Recámaras: {data.Bedrooms}
            🚿 Baños: {data.Bathrooms}
            📸 Fotos: {data.MediaIds.Count}
            📝 {data.Description}
            🎥 {video}

            ✅ Responde *confirmar* para guardar
            ✏️ O corrige un campo: *baños 1*, *precio 2500000*, *zona tulum*, *recamaras 2*, *descripcion ...*, *fotos*
            ❌ *cancelar* para descartar todo
            """;
    }

    // ---------------------------------------------------------------- navigation

    private static readonly string[] Chain =
    [
        IntakeSteps.ListingTypeStep, IntakeSteps.Type, IntakeSteps.Zone, IntakeSteps.Price,
        IntakeSteps.RentPrice, IntakeSteps.Bedrooms, IntakeSteps.Bathrooms, IntakeSteps.Photo,
        IntakeSteps.Description, IntakeSteps.Video,
    ];

    /// <summary>First unfilled field after the current step; Confirm when nothing remains.</summary>
    private static string NextMissingAfter(string currentStep, IntakeData d)
    {
        var start = Array.IndexOf(Chain, currentStep) + 1;
        for (var i = start; i < Chain.Length; i++)
        {
            if (IsMissing(Chain[i], d)) return Chain[i];
        }
        return IntakeSteps.Confirm;
    }

    private static bool IsMissing(string step, IntakeData d) => step switch
    {
        IntakeSteps.ListingTypeStep => d.ListingType is null,
        IntakeSteps.Type => d.Type is null,
        IntakeSteps.Zone => d.Zone is null,
        IntakeSteps.Price => d.ListingType == "renta" ? d.RentPrice is null : d.Price is null,
        IntakeSteps.RentPrice => d.ListingType == "ambos" && d.RentPrice is null,
        IntakeSteps.Bedrooms => d.Bedrooms is null,
        IntakeSteps.Bathrooms => d.Bathrooms is null,
        IntakeSteps.Photo => !d.PhotosDone,
        IntakeSteps.Description => string.IsNullOrWhiteSpace(d.Description),
        IntakeSteps.Video => !d.VideoDone,
        _ => false,
    };

    private static string? PreviousStep(string step, IntakeData d) => step switch
    {
        IntakeSteps.Type => IntakeSteps.ListingTypeStep,
        IntakeSteps.Zone => IntakeSteps.Type,
        IntakeSteps.Price => IntakeSteps.Zone,
        IntakeSteps.RentPrice => IntakeSteps.Price,
        IntakeSteps.Bedrooms => d.ListingType == "ambos" ? IntakeSteps.RentPrice : IntakeSteps.Price,
        IntakeSteps.Bathrooms => IntakeSteps.Bedrooms,
        IntakeSteps.Photo => IntakeSteps.Bathrooms,
        IntakeSteps.Description => IntakeSteps.Photo,
        IntakeSteps.Video => IntakeSteps.Description,
        IntakeSteps.Confirm => IntakeSteps.Video,
        _ => null,
    };

    /// <summary>The question the bot asks when (re)entering a step.</summary>
    public static string QuestionFor(string step, IntakeData d) => step switch
    {
        IntakeSteps.ListingTypeStep => "¿Es para *venta*, *renta* o *ambos*?",
        IntakeSteps.Type => "¿Qué tipo de propiedad es? Escribe: *casa*, *depto*, *terreno* o *comercial*",
        IntakeSteps.Zone => "¿En qué zona está?\n(Cancún Centro / Zona Hotelera / Playa del Carmen / Tulum / Puerto Morelos)",
        IntakeSteps.Price => d.ListingType == "renta"
            ? "¿Cuál es la renta mensual en MXN?\nSolo números, sin comas (ej: *15000*)"
            : "¿Cuál es el precio de venta en MXN?\nSolo números, sin comas (ej: *2500000*)",
        IntakeSteps.RentPrice => "¿Cuánto sería la renta mensual? Solo números (ej: *15000*)",
        IntakeSteps.Bedrooms => "¿Cuántas recámaras? (0 para estudio/loft)",
        IntakeSteps.Bathrooms => "¿Cuántos baños?",
        IntakeSteps.Photo => "Mándame las fotos de la propiedad (las que quieras, una por una). " +
                             "Cuando termines escribe *listo*, o *sin foto* para continuar 📸",
        IntakeSteps.Description => "Escribe una descripción corta (2–3 líneas, lo que más destaca — mínimo 10 caracteres):",
        IntakeSteps.Video => "¿Tienes un video o tour virtual? Pega el link (YouTube, Google Drive, etc.) o escribe *sin video*",
        IntakeSteps.Confirm => BuildSummary(d),
        _ => "",
    };

    // ---------------------------------------------------------------- helpers

    private static Result Stay(BrokerIntakeState state, IntakeData data, string step, string? reply, bool error = false) =>
        new(new BrokerIntakeState { Step = step, Data = data }, reply, Done: false, Error: error);

    private static bool TrySetZone(IntakeData data, string norm)
    {
        var zone = ZoneMap.FirstOrDefault(z => norm.Contains(z.Key));
        if (zone.Zone is null) return false;
        data.Zone = zone.Zone;
        return true;
    }

    private static int? ParseDigits(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}
