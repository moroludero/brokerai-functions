using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class ShortCodeGeneratorTests
{
    [Theory]
    [InlineData("casa", 0, "CASA-001")]
    [InlineData("casa", 1, "CASA-002")]
    [InlineData("depto", 6, "DEPTO-007")]
    [InlineData("terreno", 0, "TERR-001")]
    [InlineData("comercial", 0, "COM-001")]
    [InlineData("unknown", 0, "PROP-001")]
    [InlineData(null, 0, "PROP-001")]
    public void Generate_ProducesExpectedFormat(string? type, int existingCount, string expected)
    {
        ShortCodeGenerator.Generate(type, existingCount).Should().Be(expected);
    }

    [Fact]
    public void ConcurrentInserts_RetryWithIncrementedCount_ProducesDistinctCodes()
    {
        // Simulates the retry-on-unique-violation pattern: two "concurrent" callers
        // both see existingCount=0, first wins CASA-001, second retries at count=1.
        var first = ShortCodeGenerator.Generate("casa", 0);
        var second = ShortCodeGenerator.Generate("casa", 1); // after retry increments count

        first.Should().Be("CASA-001");
        second.Should().Be("CASA-002");
        first.Should().NotBe(second);
    }

    [Fact]
    public void QrLink_EncodesSharedNumberAndShortCode()
    {
        var link = ShortCodeGenerator.QrLink("529981234567", "CASA-001");

        link.Should().Be("https://wa.me/529981234567?text=PROP:CASA-001");
    }
}
