using BrokerAi.Core.Data;
using BrokerAi.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using BrokerAi.Core.Services;

namespace BrokerAi.Functions;

/// <summary>
/// Per-broker daily digest ("Resumen de ayer"), 8:00 Cancún (UTC-5, fixed — no DST since 2015).
/// Cron is UTC because WEBSITE_TIME_ZONE is unreliable on Linux Consumption.
/// </summary>
public sealed class DailyDigestFunction(BrokerAiDbContext db, IWhatsAppSender sender, ILogger<DailyDigestFunction> logger)
{
    [Function("DailyDigest")]
    public async Task Run([TimerTrigger("0 0 13 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var brokers = await db.Brokers.ToListAsync(ct);
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        foreach (var broker in brokers)
        {
            var leads = await db.Leads
                .Where(l => l.BrokerId == broker.Id && l.CreatedAt >= yesterday)
                .ToListAsync(ct);

            var digest = DigestBuilder.BuildBrokerDaily(leads, DateTimeOffset.UtcNow);
            if (digest is null) continue; // no activity — skip send

            var phoneNumberId = broker.PhoneNumberId;
            if (string.IsNullOrEmpty(phoneNumberId))
            {
                logger.LogWarning("Broker {BrokerId} has no phone_number_id — skipping digest", broker.Id);
                continue;
            }

            await sender.SendTextAsync(phoneNumberId, broker.AlertNumber, digest, ct);
        }
    }
}
