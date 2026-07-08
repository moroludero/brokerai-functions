using BrokerAi.Core.Services;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BrokerAi.Core.Tests;

public class CollageBuilderTests
{
    private static byte[] TestImage(int width = 400, int height = 300)
    {
        using var img = new Image<Rgb24>(width, height, new Rgb24(120, 160, 200));
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    [Fact]
    public void Build_SingleImage_ReturnsItUnchanged()
    {
        var original = TestImage();

        var result = CollageBuilder.Build([original]);

        result.Should().BeSameAs(original, "one photo needs no grid");
    }

    [Theory]
    [InlineData(2, 2, 1)] // 2 photos → 2 cols x 1 row
    [InlineData(3, 2, 2)] // 3 photos → 2 cols x 2 rows
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    public void Build_MultipleImages_ProducesExpectedGridDimensions(int count, int expectedCols, int expectedRows)
    {
        var images = Enumerable.Range(0, count).Select(_ => TestImage()).ToList();

        var collage = CollageBuilder.Build(images);

        using var result = Image.Load(collage);
        result.Width.Should().Be(expectedCols * 800 + (expectedCols - 1) * 8);
        result.Height.Should().Be(expectedRows * 600 + (expectedRows - 1) * 8);
    }

    [Fact]
    public void Build_MoreThanMax_UsesOnlyFirstSix()
    {
        var images = Enumerable.Range(0, 9).Select(_ => TestImage()).ToList();

        var collage = CollageBuilder.Build(images);

        using var result = Image.Load(collage);
        // 6 photos → 2 cols x 3 rows, same as exactly six
        result.Height.Should().Be(3 * 600 + 2 * 8);
    }

    [Fact]
    public void Build_Empty_Throws()
    {
        var act = () => CollageBuilder.Build([]);

        act.Should().Throw<ArgumentException>();
    }
}
