using System.Text.Json.Serialization;

namespace BrokerAi.Core.Domain;

/// <summary>
/// THE canonical JSON schema for every lead-facing Claude extraction call.
/// Replaces the three conflicting schemas from the old n8n design
/// (classification.txt "already_known", qualification-specific "extracted_*",
/// and the ad-hoc schema in the old workflow export).
/// </summary>
public sealed class LeadExtraction
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "vague"; // specific | vague | off_topic

    [JsonPropertyName("language")]
    public string Language { get; set; } = "es";  // es | en

    [JsonPropertyName("lead_goal")]
    public string? LeadGoal { get; set; }          // comprar | rentar | null

    [JsonPropertyName("extracted")]
    public ExtractedFields Extracted { get; set; } = new();

    /// <summary>Model-suggested reply for qualification calls; null on pure classification.</summary>
    [JsonPropertyName("reply")]
    public string? Reply { get; set; }

    public bool IsOffTopic => Intent == "off_topic";
}

public sealed class ExtractedFields
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("budget_min")]
    public int? BudgetMin { get; set; }

    [JsonPropertyName("budget_max")]
    public int? BudgetMax { get; set; }

    [JsonPropertyName("zone")]
    public string? Zone { get; set; }              // one of Zones.All

    [JsonPropertyName("property_type")]
    public string? PropertyType { get; set; }      // casa | depto | terreno | comercial

    [JsonPropertyName("visit_availability")]
    public string? VisitAvailability { get; set; } // free text
}
