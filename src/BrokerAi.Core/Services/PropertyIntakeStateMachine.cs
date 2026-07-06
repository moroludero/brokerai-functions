using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>
/// Port of 05-broker-intake.js — advances the broker property-intake
/// conversation one step at a time. Pure: state in, state + reply out.
/// Steps: listing_type → type → zone → price → (rent_price) → bedrooms
///        → bathrooms → photo → description → video → done
/// </summary>
public static class PropertyIntakeStateMachine
{
    public sealed record Result(BrokerIntakeState NextState, string? Reply, bool Done, bool Error);

    private static readonly string[] ValidTypes = ["casa", "depto", "departamento", "terreno", "comercial"];

    private static readonly (string Key, string Zone)[] ZoneMap =
    [
        ("cancun", Zones.CancunCentro),
        ("hotelera", Zones.ZonaHotelera),
        ("playa", Zones.PlayaDelCarmen),
        ("tulum", Zones.Tulum),
        ("morelos", Zones.PuertoMorelos),
    ];

    public static Result Advance(BrokerIntakeState state, string? text, string? mediaId)
    {
        var input = (text ?? "").Trim();
        var norm = TextNormalizer.Normalize(input);
        var data = state.Data;
        var step = state.Step;
        var nextStep = step;
        string? reply = null;
        var error = false;

        switch (step)
        {
            case IntakeSteps.ListingTypeStep:
                if (norm.Contains("venta") || norm.Contains("vender") || norm.Contains("compra"))
                    data.ListingType = "venta";
                else if (norm.Contains("renta") || norm.Contains("rentar") || norm.Contains("alquil"))
                    data.ListingType = "renta";
                else if (norm.Contains("ambos") || norm.Contains("los dos") || norm.Contains("ambas"))
                    data.ListingType = "ambos";
                else
                {
                    reply = "¿Es para *venta*, *renta* o *ambos*?";
                    error = true;
                    break;
                }
                nextStep = IntakeSteps.Type;
                reply = "¿Qué tipo de propiedad es? Escribe: *casa*, *depto*, *terreno* o *comercial*";
                break;

            case IntakeSteps.Type:
                var match = ValidTypes.FirstOrDefault(t => norm.Contains(t));
                if (match is null)
                {
                    reply = "¿Qué tipo de propiedad es? Escribe: *casa*, *depto*, *terreno* o *comercial*";
                    error = true;
                }
                else
                {
                    data.Type = match == "departamento" ? "depto" : match;
                    nextStep = IntakeSteps.Zone;
                    reply = "¿En qué zona está?\n(Cancún Centro / Zona Hotelera / Playa del Carmen / Tulum / Puerto Morelos)";
                }
                break;

            case IntakeSteps.Zone:
                var zone = ZoneMap.FirstOrDefault(z => norm.Contains(z.Key));
                if (zone.Zone is null)
                {
                    reply = "No reconocí la zona. Opciones:\n• Cancún Centro\n• Zona Hotelera\n• Playa del Carmen\n• Tulum\n• Puerto Morelos";
                    error = true;
                }
                else
                {
                    data.Zone = zone.Zone;
                    nextStep = IntakeSteps.Price;
                    var priceLabel = data.ListingType == "renta" ? "renta mensual" : "precio de venta";
                    var example = data.ListingType == "renta" ? "*15000*" : "*2500000*";
                    reply = $"¿Cuál es el {priceLabel} en MXN?\nSolo números, sin comas (ej: {example})";
                }
                break;

            case IntakeSteps.Price:
                var priceNum = ParseDigits(input);
                // FIX vs old design: the old n8n node applied the sale-price minimum
                // (50,000) even when this step asks for a monthly RENT (listing_type
                // "renta"), which would reject every normal rental under $50k/month —
                // effectively blocking the renta-only intake path entirely. Use the
                // rent minimum (1,000) for renta here, matching the dedicated
                // rent_price step used by the "ambos" path below.
                var minPrice = data.ListingType == "renta" ? 1_000 : 50_000;
                if (priceNum is null || priceNum < minPrice)
                {
                    reply = data.ListingType == "renta"
                        ? "Necesito el precio de renta mensual en MXN.\nEjemplo: *15000*"
                        : "Necesito el precio en pesos MXN, solo números.\nEjemplo: *2500000*";
                    error = true;
                }
                else if (data.ListingType == "renta")
                {
                    // For renta, the single price asked here is the monthly rent
                    data.Price = null;
                    data.RentPrice = priceNum;
                    nextStep = IntakeSteps.Bedrooms;
                    reply = "¿Cuántas recámaras?";
                }
                else if (data.ListingType == "ambos")
                {
                    data.Price = priceNum;
                    nextStep = IntakeSteps.RentPrice;
                    reply = "¿Cuánto sería la renta mensual? Solo números (ej: *15000*)";
                }
                else
                {
                    data.Price = priceNum;
                    nextStep = IntakeSteps.Bedrooms;
                    reply = "¿Cuántas recámaras?";
                }
                break;

            case IntakeSteps.RentPrice:
                var rentNum = ParseDigits(input);
                if (rentNum is null || rentNum < 1_000)
                {
                    reply = "Necesito el precio de renta mensual en MXN.\nEjemplo: *15000*";
                    error = true;
                }
                else
                {
                    data.RentPrice = rentNum;
                    nextStep = IntakeSteps.Bedrooms;
                    reply = "¿Cuántas recámaras?";
                }
                break;

            case IntakeSteps.Bedrooms:
                if (!int.TryParse(input, out var bedrooms) || bedrooms < 0 || bedrooms > 20)
                {
                    reply = "¿Cuántas recámaras? Escribe un número (0 para estudio/loft)";
                    error = true;
                }
                else
                {
                    data.Bedrooms = bedrooms;
                    nextStep = IntakeSteps.Bathrooms;
                    reply = "¿Cuántos baños?";
                }
                break;

            case IntakeSteps.Bathrooms:
                if (!int.TryParse(input, out var bathrooms) || bathrooms < 1 || bathrooms > 20)
                {
                    reply = "¿Cuántos baños? Escribe un número";
                    error = true;
                }
                else
                {
                    data.Bathrooms = bathrooms;
                    nextStep = IntakeSteps.Photo;
                    reply = "Mándame una foto de la propiedad, o escribe *sin foto* para continuar 📸";
                }
                break;

            case IntakeSteps.Photo:
                if (!string.IsNullOrEmpty(mediaId))
                {
                    data.MediaId = mediaId; // uploaded to Blob later → image_url
                    nextStep = IntakeSteps.Description;
                    reply = "Perfecto 👍 Ahora escribe una descripción corta (2–3 líneas, lo que más destaca):";
                }
                else if (norm.Contains("sin foto") || norm.Contains("no foto"))
                {
                    data.ImageUrl = null;
                    nextStep = IntakeSteps.Description;
                    reply = "Ok, sin foto. Escribe una descripción corta de la propiedad:";
                }
                else
                {
                    reply = "Mándame una foto o escribe *sin foto*";
                    error = true;
                }
                break;

            case IntakeSteps.Description:
                if (input.Length < 10)
                {
                    reply = "Escribe una descripción un poco más larga (mínimo 10 caracteres)";
                    error = true;
                }
                else
                {
                    data.Description = input;
                    nextStep = IntakeSteps.Video;
                    reply = "¿Tienes un video o tour virtual? Pega el link (YouTube, Google Drive, etc.) o escribe *sin video*";
                }
                break;

            case IntakeSteps.Video:
                if (norm.Contains("sin video") || norm.Contains("no video") || norm.Contains("no tengo"))
                    data.VideoUrl = null;
                else if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    data.VideoUrl = input;
                else
                {
                    reply = "Pega el link del video (debe empezar con http) o escribe *sin video*";
                    error = true;
                    break;
                }
                nextStep = IntakeSteps.Done;
                reply = null; // final confirmation is sent by the save-property flow
                break;
        }

        var done = nextStep == IntakeSteps.Done;
        var nextState = new BrokerIntakeState { Step = nextStep, Data = data };
        return new Result(nextState, reply, done, error);
    }

    private static int? ParseDigits(string text)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n : null;
    }
}
