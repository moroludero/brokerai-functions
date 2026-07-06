using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Data.Entities;

public class AdCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BrokerId { get; set; }
    public Broker? Broker { get; set; }

    public Guid? PropertyId { get; set; }
    public Property? Property { get; set; }

    public string? ShortCode { get; set; }
    public string? FbPostId { get; set; }
    public string? FbCampaignId { get; set; }
    public string? FbAdsetId { get; set; }
    public string? FbAdId { get; set; }

    public int DurationDays { get; set; } = 7;

    /// <summary>Amount spent on Meta (MXN).</summary>
    public int? BudgetMxn { get; set; }

    /// <summary>Amount invoiced to the broker (budget + markup). Fixed: computed at creation, unlike the old design.</summary>
    public int? BilledMxn { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
