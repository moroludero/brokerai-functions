using BrokerAi.Core.Data;
using BrokerAi.Core.Domain;
using BrokerAi.Core.Options;
using BrokerAi.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrokerAi.Functions;

/// <summary>
/// Owner-facing weekly digest across all brokers (Workflow 4), Monday 9:00 Cancún.
/// Cron is UTC (Cancún fixed UTC-5): 9am Mon Cancún = 14:00 UTC Mon.
/// </summary>
public sealed class AdminWeeklyDigestFunction(
    BrokerAiDbContext db,
    IWhatsAppSender sender,
    IOptions<AppOptions> appOptions,
    IOptions<MetaOptions> metaOptions,
    ILogger<AdminWeeklyDigestFunction> logger)
{
    [Function("AdminWeeklyDigest")]
    public async Task Run([TimerTrigger("0 0 14 * * 1")] TimerInfo timer, CancellationToken ct)
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var brokers = await db.Brokers.ToListAsync(ct);

        var rows = new List<DigestBuilder.BrokerWeekStats>();
        foreach (var broker in brokers)
        {
            var leadsWeek = await db.Leads.CountAsync(l => l.BrokerId == broker.Id && l.CreatedAt >= weekAgo, ct);
            var hotWeek = await db.Leads.CountAsync(l =>
                l.BrokerId == broker.Id && l.CreatedAt >= weekAgo && l.Score >= LeadScorer.HotThreshold, ct);
            var propertiesActive = await db.Properties.CountAsync(p => p.BrokerId == broker.Id && p.Active, ct);

            rows.Add(new DigestBuilder.BrokerWeekStats(broker.Name, broker.Plan, leadsWeek, hotWeek, propertiesActive));
        }

        var message = DigestBuilder.BuildAdminWeekly(rows);

        var ownerNumber = appOptions.Value.OwnerAlertNumber;
        var anyPhoneNumberId = brokers.FirstOrDefault(b => b.PhoneNumberId is not null)?.PhoneNumberId;
        if (string.IsNullOrEmpty(ownerNumber) || anyPhoneNumberId is null)
        {
            logger.LogWarning("Cannot send admin weekly digest — missing owner number or no broker phone_number_id configured");
            return;
        }

        await sender.SendTextAsync(anyPhoneNumberId, ownerNumber, message, ct);
    }
}
