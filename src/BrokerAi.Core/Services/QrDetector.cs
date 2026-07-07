using System.Text.RegularExpressions;

namespace BrokerAi.Core.Services;

/// <summary>
/// Detects a property short code in the lead's message. Runs before any AI call.
/// The cartel QR pre-fills a natural sentence containing the code ("Hola! Me
/// interesa la propiedad CASA-001..."), so the code is matched anywhere in the
/// text — leads are less likely to delete a message that reads like a human
/// wrote it. The legacy bare "PROP:CASA-001" format still matches.
/// </summary>
public static partial class QrDetector
{
    // Known short-code prefixes + 1-4 digits, anywhere in the message.
    // Word boundaries keep casual words ("casa") from matching — the dash+digits
    // shape (CASA-001) is what makes it a code.
    [GeneratedRegex(@"\b(CASA|DEPTO|TERR|COM|PROP)-(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();

    /// <summary>Returns the short code (uppercased) if the message references one, else null.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CodePattern().Match(text);
        return match.Success
            ? $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}"
            : null;
    }
}
