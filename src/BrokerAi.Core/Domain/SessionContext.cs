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

    /// <summary>Broker session: short code of the property currently receiving extra photos ("fotos CASA-001" mode).</summary>
    [JsonPropertyName("photo_add_short_code")]
    public string? PhotoAddShortCode { get; set; }

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

    /// <summary>Meta media ids of every photo sent during intake (uploaded to Blob on completion).</summary>
    [JsonPropertyName("media_ids")]
    public List<string> MediaIds { get; set; } = [];

    /// <summary>Photo step completed ("listo" / "sin foto") — MediaIds alone can't tell an intentional skip.</summary>
    [JsonPropertyName("photos_done")]
    public bool PhotosDone { get; set; }

    // Property location (native WhatsApp location share, or a typed address)
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>Location step completed (pin shared, address typed, or "sin ubicación").</summary>
    [JsonPropertyName("location_done")]
    public bool LocationDone { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }

    /// <summary>Video step completed (link or "sin video") — VideoUrl null can't tell an intentional skip.</summary>
    [JsonPropertyName("video_done")]
    public bool VideoDone { get; set; }
}

public static class IntakeSteps
{
    public const string ListingTypeStep = "listing_type";
    public const string Type = "type";
    public const string Zone = "zone";
    public const string Location = "location";
    public const string Price = "price";
    public const string RentPrice = "rent_price";
    public const string Bedrooms = "bedrooms";
    public const string Bathrooms = "bathrooms";
    public const string Photo = "photo";
    public const string Description = "description";
    public const string Video = "video";
    public const string Confirm = "confirm";
    public const string Done = "done";
}
