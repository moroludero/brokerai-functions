namespace BrokerAi.Core.Data.Entities;

/// <summary>
/// One broker alert per (lead, property) — industry-standard lead handling:
/// the person is one contact, but interest in each listing is tracked (and
/// alerted) separately. PropertyId null = generic (non-QR) qualification alert,
/// deduped once per lead by the unique index (SQL Server treats NULLs as equal).
/// </summary>
public class LeadAlert
{
    public long Id { get; set; }
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public Guid? PropertyId { get; set; }
    public Property? Property { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
