using System.Text.RegularExpressions;

namespace BrokerAi.Core.Services;

/// <summary>
/// Port of 04-broker-command.js merged with WORKFLOW-STEPS.md's first-word command set.
/// Canonical commands: ayuda, listar, agregar, pausar, activar, publicar, publicidad, resumen.
/// Old phrases kept as aliases. Unrecognized input falls through to advisor mode.
/// </summary>
public static partial class BrokerCommandRouter
{
    public enum Command
    {
        Advisor,        // fallthrough — free text to the AI advisor
        Ayuda,
        Listar,         // alias: mis propiedades
        Agregar,        // alias: nueva propiedad
        Pausar,         // alias: desactivar [código]
        Activar,        // reactivate a property
        Publicar,       // organic Facebook post
        Publicidad,     // paid Facebook ad
        Resumen,        // alias: mis leads
        Fotos,          // add photos to an existing property
    }

    public sealed record Detection(Command Command, string? ShortCode, int? BudgetMxn, int DurationDays);

    public static Detection Detect(string? text)
    {
        var norm = TextNormalizer.Normalize(text ?? "");

        if (Regex.IsMatch(norm, @"^(ayuda|menu|opciones|comandos|help)$"))
            return new(Command.Ayuda, null, null, 7);

        // Before Agregar: "agregar fotos CASA-001" must not fall into property intake.
        if (Regex.IsMatch(norm, @"^(agregar\s+)?fotos?\s+\S"))
            return new(Command.Fotos, QrDetector.Detect(norm), null, 7);

        if (norm.StartsWith("agregar") || Regex.IsMatch(norm, @"^nueva\s*propiedad"))
            return new(Command.Agregar, null, null, 7);

        if (norm.StartsWith("listar") || Regex.IsMatch(norm, @"^mis\s*propiedades?"))
            return new(Command.Listar, null, null, 7);

        if (norm.StartsWith("resumen") || Regex.IsMatch(norm, @"^mis\s*leads?"))
            return new(Command.Resumen, null, null, 7);

        if (Regex.IsMatch(norm, @"^(pausar|desactivar)\s+\S"))
            return new(Command.Pausar, TailCode(norm), null, 7);

        if (Regex.IsMatch(norm, @"^activar\s+\S"))
            return new(Command.Activar, TailCode(norm), null, 7);

        if (Regex.IsMatch(norm, @"^publicar\s+\S"))
            return new(Command.Publicar, TailCode(norm), null, 7);

        if (Regex.IsMatch(norm, @"^publicidad\s+\S"))
        {
            // "publicidad CASA-001 500 7" → code, budget MXN (>100), duration days (<=30)
            var parts = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            var shortCode = parts[0].ToUpperInvariant();
            int? budget = null;
            var days = 7;
            foreach (var p in parts.Skip(1))
            {
                if (!int.TryParse(p, out var n)) continue;
                if (n > 100 && budget is null) budget = n;
                else if (n <= 30) days = n;
            }
            return new(Command.Publicidad, shortCode, budget, days);
        }

        return new(Command.Advisor, null, null, 7);
    }

    private static string TailCode(string norm) =>
        norm[(norm.IndexOf(' ') + 1)..].Trim().ToUpperInvariant();

    public const string HelpMessage =
        """
        🤖 *Comandos disponibles:*

        🏠 *agregar* — agregar una propiedad
        📸 *fotos [código]* — agregar fotos a una propiedad, ej: fotos CASA-001
        📋 *listar* — ver tus propiedades activas
        📊 *resumen* — ver resumen de leads de hoy
        🔇 *pausar [código]* — ej: pausar CASA-001
        🔔 *activar [código]* — reactivar una propiedad
        📤 *publicar [código]* — post gratis en Facebook
        📣 *publicidad [código] [presupuesto]* — anuncio pagado
           ej: publicidad CASA-001 500 (anuncio $500 MXN)

        💬 *Cualquier otra pregunta* — te respondo con datos de tus leads y propiedades
        """;
}
