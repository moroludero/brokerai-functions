using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Data.Entities;

public class Broker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>Dedicated bot number; null during pilot (shared number).</summary>
    public string? WhatsappNumber { get; set; }

    /// <summary>Broker's personal WhatsApp — receives alerts and digests. E.164 without '+'.</summary>
    public required string AlertNumber { get; set; }

    /// <summary>Meta PHONE_NUMBER_ID for multi-tenant routing; null during pilot.</summary>
    public string? PhoneNumberId { get; set; }

    public string Language { get; set; } = "es";
    public PlanTier Plan { get; set; } = PlanTier.Basico;
    public int LeadsLimit { get; set; } = 150;
    public int PropertiesLimit { get; set; } = 20;

    /// <summary>Prepaid ad credits (MXN) set when the broker pays.</summary>
    public int MonthlyAdBudget { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Lead> Leads { get; set; } = [];
    public List<Property> Properties { get; set; } = [];
}
