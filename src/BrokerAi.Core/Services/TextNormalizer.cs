using System.Globalization;
using System.Text;

namespace BrokerAi.Core.Services;

/// <summary>
/// Accent-stripping normalization used across command detection and intake parsing.
/// Single implementation replaces the fragile regex duplicated in the old
/// 04-broker-command.js and 05-broker-intake.js.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        // Collapse whitespace runs to single spaces
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
