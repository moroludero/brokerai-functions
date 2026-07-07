namespace BrokerAi.Core.Services;

/// <summary>
/// Port of 07-short-code-gen.js — generates short codes like CASA-001, DEPTO-007.
/// FIXES vs old design: no process.env (config injected by caller), and the dead
/// chart.googleapis.com QR endpoint is replaced with api.qrserver.com.
/// </summary>
public static class ShortCodeGenerator
{
    private static readonly Dictionary<string, string> TypePrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["casa"] = "CASA",
        ["depto"] = "DEPTO",
        ["terreno"] = "TERR",
        ["comercial"] = "COM",
    };

    /// <summary>
    /// Builds the short code from the property type and the count of existing
    /// properties of that type for the broker. Caller retries with count+1 on a
    /// unique-index violation (concurrency-safe without locks).
    /// </summary>
    public static string Generate(string? propertyType, int existingCountOfType)
    {
        var prefix = propertyType is not null && TypePrefix.TryGetValue(propertyType, out var p) ? p : "PROP";
        return $"{prefix}-{existingCountOfType + 1:D3}";
    }

    /// <summary>
    /// Pre-filled text the QR puts in the lead's WhatsApp. Reads like something a
    /// person would send (so they hit Send instead of deleting it) while still
    /// carrying the short code QrDetector extracts.
    /// </summary>
    public static string QrPrefillText(string shortCode) =>
        $"¡Hola! 👋 Vi el anuncio de la propiedad {shortCode} y me gustaría recibir más información 🏠";

    /// <summary>wa.me deep link the cartel QR encodes. botNumber = broker's own number or the shared pilot number.</summary>
    public static string QrLink(string botNumber, string shortCode) =>
        $"https://wa.me/{botNumber}?text={Uri.EscapeDataString(QrPrefillText(shortCode))}";

    public static string QrImageUrl(string botNumber, string shortCode) =>
        $"https://api.qrserver.com/v1/create-qr-code/?size=300x300&data={Uri.EscapeDataString(QrLink(botNumber, shortCode))}";
}
