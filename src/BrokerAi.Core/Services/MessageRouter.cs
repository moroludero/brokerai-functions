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
        // Meta reports Mexican numbers inconsistently: sometimes with the legacy
        // mobile '1' after the country code (521XXXXXXXXXX), sometimes without
        // (52XXXXXXXXXX). Match the broker's alert number under both spellings.
        var fromAlt = MxAlternate(from);

        var broker = await db.Brokers.FirstOrDefaultAsync(b =>
            b.PhoneNumberId == phoneNumberId ||
            b.AlertNumber == from ||
            b.AlertNumber == fromAlt, ct);

        if (broker is null) return new RouteResult(null, false);

        var isBroker = from == broker.AlertNumber || fromAlt == broker.AlertNumber;
        return new RouteResult(broker, isBroker);
    }

    /// <summary>Returns the other Mexican spelling of the number (52↔521), or the input unchanged.</summary>
    internal static string MxAlternate(string number)
    {
        if (number.StartsWith("521") && number.Length == 13)
            return "52" + number[3..];          // 521XXXXXXXXXX → 52XXXXXXXXXX
        if (number.StartsWith("52") && number.Length == 12)
            return "521" + number[2..];         // 52XXXXXXXXXX → 521XXXXXXXXXX
        return number;
    }
}
