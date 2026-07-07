using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class QrDetectorTests
{
    [Theory]
    [InlineData("PROP-001", "PROP-001")]
    [InlineData("casa-001", "CASA-001")]
    [InlineData("  DEPTO-007  ", "DEPTO-007")]
    public void Detect_BareCode_ReturnsUppercaseCode(string input, string expected)
    {
        QrDetector.Detect(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("¡Hola! 👋 Vi el anuncio de la propiedad CASA-001 y me gustaría recibir más información 🏠", "CASA-001")]
    [InlineData("me interesa la CASA-001", "CASA-001")]
    [InlineData("info del terr-002 porfa", "TERR-002")]
    [InlineData("PROP:CASA-001", "CASA-001")] // legacy bare format still matches
    public void Detect_CodeInsideNaturalSentence_ReturnsCode(string input, string expected)
    {
        QrDetector.Detect(input).Should().Be(expected);
    }

    [Fact]
    public void Detect_QrPrefillText_RoundTripsWithGenerator()
    {
        // The exact text the cartel QR pre-fills MUST be detectable.
        var prefill = ShortCodeGenerator.QrPrefillText("DEPTO-012");

        QrDetector.Detect(prefill).Should().Be("DEPTO-012");
    }

    [Theory]
    [InlineData("Hola, quiero información sobre casas")] // casual word, no dash+digits
    [InlineData("busco casa en Tulum")]
    [InlineData("tengo un presupuesto de 2-300")] // digits-dash but no known prefix
    [InlineData("")]
    [InlineData(null)]
    public void Detect_NoCode_ReturnsNull(string? input)
    {
        QrDetector.Detect(input).Should().BeNull();
    }
}
