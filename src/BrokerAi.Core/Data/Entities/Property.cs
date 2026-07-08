using BrokerAi.Core.Domain;

namespace BrokerAi.Core.Data.Entities;

public class Property
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BrokerId { get; set; }
    public Broker? Broker { get; set; }

    public required string Title { get; set; }
    public string? Zone { get; set; }
    public PropertyKind? Kind { get; set; }
    public ListingType ListingKind { get; set; } = ListingType.Venta;

    /// <summary>Sale price MXN (venta/ambos).</summary>
    public int? Price { get; set; }

    /// <summary>Monthly rent MXN (renta/ambos).</summary>
    public int? RentPrice { get; set; }

    public int? Bedrooms { get; set; }
    public int? Bathrooms { get; set; }
    public string? Description { get; set; }

    /// <summary>Cover photo (= first image). All photos live in <see cref="Images"/>.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Grid collage of up to 6 photos, regenerated whenever photos change — the single image the lead's property card uses.</summary>
    public string? CollageUrl { get; set; }

    public string? VideoUrl { get; set; }

    public List<PropertyImage> Images { get; set; } = [];

    /// <summary>e.g. "CASA-001" — printed on the cartel QR.</summary>
    public string? ShortCode { get; set; }

    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
