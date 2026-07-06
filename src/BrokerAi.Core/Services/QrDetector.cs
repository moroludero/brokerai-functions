using System.Text.RegularExpressions;

namespace BrokerAi.Core.Services;

/// <summary>
/// Port of 02-lead-qr-detect.js — detects "PROP:CASA-001" messages from
/// cartel QR scans. Runs before any AI call.
/// </summary>
public static partial class QrDetector
{
    [GeneratedRegex(@"^PROP:([A-Z0-9-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QrPattern();

    /// <summary>Returns the short code (uppercased) if the message is a QR scan, else null.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = QrPattern().Match(text.Trim());
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
