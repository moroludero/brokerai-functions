using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BrokerAi.Core.Services;

/// <summary>
/// Composes a real-estate-portal-style photo grid so the lead receives ONE
/// image containing several photos (the Cloud API cannot send albums — one
/// media per message is a documented platform limitation).
/// </summary>
public static class CollageBuilder
{
    /// <summary>Photos beyond this count don't fit readably in a phone-width grid — send them individually.</summary>
    public const int MaxPhotos = 6;

    private const int CellWidth = 800;
    private const int CellHeight = 600;
    private const int Gap = 8;

    /// <summary>
    /// Builds a 2-column cover-cropped JPEG grid from up to <see cref="MaxPhotos"/> images.
    /// Single image → returned re-encoded as-is (no grid needed).
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> imageBytes)
    {
        if (imageBytes.Count == 0) throw new ArgumentException("At least one image is required", nameof(imageBytes));

        var photos = imageBytes.Take(MaxPhotos).ToList();
        if (photos.Count == 1) return photos[0];

        var columns = 2;
        var rows = (int)Math.Ceiling(photos.Count / (double)columns);
        var canvasWidth = columns * CellWidth + (columns - 1) * Gap;
        var canvasHeight = rows * CellHeight + (rows - 1) * Gap;

        using var canvas = new Image<Rgb24>(canvasWidth, canvasHeight, new Rgb24(255, 255, 255));

        for (var i = 0; i < photos.Count; i++)
        {
            using var photo = Image.Load<Rgb24>(photos[i]);
            // Cover-crop: fill the cell completely, cropping overflow (portal style).
            photo.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CellWidth, CellHeight),
                Mode = ResizeMode.Crop,
            }));

            var col = i % columns;
            var row = i / columns;
            var position = new Point(col * (CellWidth + Gap), row * (CellHeight + Gap));
            canvas.Mutate(x => x.DrawImage(photo, position, 1f));
        }

        using var output = new MemoryStream();
        canvas.Save(output, new JpegEncoder { Quality = 82 });
        return output.ToArray();
    }
}
