using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class QrDetectorTests
{
    [Theory]
    [InlineData("PROP:CASA-001", "CASA-001")]
    [InlineData("prop:depto-007", "DEPTO-007")]
    [InlineData("  PROP:TERR-002  ", "TERR-002")]
    public void Detect_ValidQrText_ReturnsUppercaseCode(string input, string expected)
    {
        QrDetector.Detect(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Hola, quiero información sobre casas")]
    [InlineData("PROP:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("me interesa PROP:CASA-001")] // must be the whole message, not embedded
    public void Detect_NonQrText_ReturnsNull(string? input)
    {
        QrDetector.Detect(input).Should().BeNull();
    }
}
