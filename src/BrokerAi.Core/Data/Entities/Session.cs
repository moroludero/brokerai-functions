using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Data.Entities;

public class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for broker sessions.</summary>
    public Guid? LeadId { get; set; }
    public Lead? Lead { get; set; }

    /// <summary>E.164 without '+'.</summary>
    public required string Phone { get; set; }

    public Guid BrokerId { get; set; }
    public Broker? Broker { get; set; }

    public SessionType Type { get; set; } = SessionType.Lead;
    public string Step { get; set; } = LeadSteps.Greeting;

    /// <summary>Serialized SessionContext (nvarchar(max) JSON column).</summary>
    public SessionContext Context { get; set; } = new();

    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
}
