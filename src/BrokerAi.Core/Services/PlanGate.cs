using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Services;

/// <summary>Port of 00c-plan-gate.js — feature, limit, and ad-budget enforcement per plan tier.</summary>
public static class PlanGate
{
    public sealed record Request(
        string Feature,                 // new_lead | new_property | facebook_post | facebook_ad | multi_number
        PlanTier Plan,
        int LeadsThisMonth = 0,
        int PropertiesActive = 0,
        int MonthlyAdBudget = 0,        // MXN paid for ads this month
        int AdSpentThisMonth = 0,       // MXN already spent
        int RequestedBudget = 0);       // MXN requested for this ad

    public sealed record Result(bool Allowed, string Reason, int AdAvailableMxn, string? UpgradeMessage);

    private sealed record Limits(int Leads, int Properties);

    private static readonly Dictionary<PlanTier, Limits> PlanLimits = new()
    {
        [PlanTier.Basico] = new(150, 20),
        [PlanTier.Pro] = new(400, 50),
        [PlanTier.Agencia] = new(int.MaxValue, int.MaxValue),
    };

    private static readonly Dictionary<string, PlanTier[]> Features = new()
    {
        ["facebook_post"] = [PlanTier.Pro, PlanTier.Agencia],
        ["facebook_ad"] = [PlanTier.Agencia],
        ["multi_number"] = [PlanTier.Agencia],
    };

    public static Result Check(Request req)
    {
        var limits = PlanLimits[req.Plan];
        var adAvailable = req.MonthlyAdBudget - req.AdSpentThisMonth;
        var planName = req.Plan.ToString().ToLowerInvariant();
        var allowed = true;
        var reason = "";

        switch (req.Feature)
        {
            case "new_lead":
                if (req.LeadsThisMonth >= limits.Leads)
                {
                    allowed = false;
                    reason = $"límite de leads ({limits.Leads}/mes en plan {planName})";
                }
                break;

            case "new_property":
                if (req.PropertiesActive >= limits.Properties)
                {
                    allowed = false;
                    reason = $"límite de propiedades ({limits.Properties} activas en plan {planName})";
                }
                break;

            case "facebook_post":
            case "multi_number":
                if (!Features[req.Feature].Contains(req.Plan))
                {
                    allowed = false;
                    reason = $"esta función no está incluida en el plan {planName}";
                }
                break;

            case "facebook_ad":
                if (!Features["facebook_ad"].Contains(req.Plan))
                {
                    allowed = false;
                    reason = $"esta función no está incluida en el plan {planName}";
                }
                else if (req.MonthlyAdBudget == 0)
                {
                    allowed = false;
                    reason = "no_ad_budget";
                }
                else if (req.RequestedBudget > adAvailable)
                {
                    allowed = false;
                    reason = "insufficient_ad_budget";
                }
                break;

            default:
                // FIX vs old design: unknown feature names no longer silently pass.
                throw new ArgumentException($"Unknown plan-gated feature: '{req.Feature}'", nameof(req));
        }

        string? upgradeMessage = null;
        if (!allowed)
        {
            var nextPlan = req.Plan switch
            {
                PlanTier.Basico => "Pro ($1,200 MXN/mes)",
                PlanTier.Pro => "Agencia ($2,500 MXN/mes)",
                _ => null,
            };
            var upgradeHint = nextPlan is not null
                ? $"Escríbenos para pasarte al plan {nextPlan} 🚀"
                : "";

            upgradeMessage = reason switch
            {
                "insufficient_ad_budget" =>
                    $"⚠️ Saldo insuficiente para este anuncio.\n" +
                    $"💰 Saldo disponible: ${adAvailable.ToString("N0", MxCulture)} MXN\n" +
                    $"📣 Anuncio solicitado: ${req.RequestedBudget.ToString("N0", MxCulture)} MXN\n\n" +
                    "Recarga tu saldo de anuncios o reduce el presupuesto.",
                "no_ad_budget" =>
                    "⚠️ No tienes saldo de anuncios este mes.\n" +
                    "Contáctanos para agregar presupuesto publicitario a tu cuenta.",
                _ when req.Plan == PlanTier.Agencia => $"⚠️ {reason}.",
                _ => $"⚠️ Alcanzaste el {reason} de tu plan.\n{upgradeHint}",
            };
        }

        return new Result(allowed, reason, adAvailable, upgradeMessage);
    }

    private static readonly System.Globalization.CultureInfo MxCulture =
        System.Globalization.CultureInfo.GetCultureInfo("es-MX");
}
