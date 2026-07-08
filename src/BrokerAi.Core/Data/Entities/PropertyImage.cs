namespace BrokerAi.Core.Data.Entities;

/// <summary>
/// One photo of a property. Properties usually have several; Property.ImageUrl
/// stays as the cover (= first image) for cards and Facebook posts.
/// </summary>
public class PropertyImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public Property? Property { get; set; }

    public required string Url { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
