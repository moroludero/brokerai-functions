using System.Text.Json.Serialization;

namespace BrokerAi.Core.Domain;

/// <summary>Typed model for the sessions.context JSON column.</summary>
public sealed class SessionContext
{
    [JsonPropertyName("history")]
    public List<TurnRecord> History { get; set; } = [];

    [JsonPropertyName("broker_intake")]
    public BrokerIntakeState? BrokerIntake { get; set; }

    [JsonPropertyName("qr_short_code")]
    public string? QrShortCode { get; set; }

    [JsonPropertyName("last_message_id")]
    public string? LastMessageId { get; set; }
}

public sealed class TurnRecord
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user"; // "user" | "assistant"

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>State for the broker property-intake conversation (port of sessions.context.broker_intake).</summary>
public sealed class BrokerIntakeState
{
    [JsonPropertyName("step")]
    public string Step { get; set; } = IntakeSteps.ListingTypeStep;

    [JsonPropertyName("data")]
    public IntakeData Data { get; set; } = new();
}

public sealed class IntakeData
{
    [JsonPropertyName("listing_type")]
    public string? ListingType { get; set; }   // venta | renta | ambos

    [JsonPropertyName("type")]
    public string? Type { get; set; }          // casa | depto | terreno | comercial

    [JsonPropertyName("zone")]
    public string? Zone { get; set; }

    [JsonPropertyName("price")]
    public int? Price { get; set; }

    [JsonPropertyName("rent_price")]
    public int? RentPrice { get; set; }

    [JsonPropertyName("bedrooms")]
    public int? Bedrooms { get; set; }

    [JsonPropertyName("bathrooms")]
    public int? Bathrooms { get; set; }

    [JsonPropertyName("media_id")]
    public string? MediaId { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }
}

public static class IntakeSteps
{
    public const string ListingTypeStep = "listing_type";
    public const string Type = "type";
    public const string Zone = "zone";
    public const string Price = "price";
    public const string RentPrice = "rent_price";
    public const string Bedrooms = "bedrooms";
    public const string Bathrooms = "bathrooms";
    public const string Photo = "photo";
    public const string Description = "description";
    public const string Video = "video";
    public const string Done = "done";
}
