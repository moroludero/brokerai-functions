using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Data.Entities;

public class Lead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BrokerId { get; set; }
    public Broker? Broker { get; set; }

    /// <summary>E.164 without '+'.</summary>
    public required string Phone { get; set; }

    public string? Name { get; set; }
    public string Language { get; set; } = "es";
    public int? BudgetMin { get; set; }
    public int? BudgetMax { get; set; }
    public string? Zone { get; set; }
    public string? PropertyType { get; set; }

    /// <summary>Free text: "jueves tarde o viernes mañana".</summary>
    public string? VisitAvailability { get; set; }

    public LeadGoal? Goal { get; set; }
    public int Score { get; set; }
    public bool AlertSent { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
