using BrokerAi.Core.Data;
using BrokerAi.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BrokerAi.Core.Services;

/// <summary>
/// Port of 01-route-message.js — resolves the sending number to a broker
/// (by phone_number_id or, during pilot, by alert_number), and determines
/// whether the sender is that broker themself or a lead.
/// </summary>
public sealed class MessageRouter(BrokerAiDbContext db)
{
    public sealed record RouteResult(Broker? Broker, bool IsBroker);

    public async Task<RouteResult> ResolveAsync(string phoneNumberId, string from, CancellationToken ct = default)
    {
        var broker = await db.Brokers
            .FirstOrDefaultAsync(b => b.PhoneNumberId == phoneNumberId || b.AlertNumber == from, ct);

        if (broker is null) return new RouteResult(null, false);

        var isBroker = from == broker.AlertNumber;
        return new RouteResult(broker, isBroker);
    }
}
