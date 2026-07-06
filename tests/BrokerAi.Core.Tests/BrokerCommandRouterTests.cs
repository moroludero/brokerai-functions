using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;
using static BrokerAi.Core.Services.BrokerCommandRouter;

namespace BrokerAi.Core.Tests;

public class BrokerCommandRouterTests
{
    [Theory]
    [InlineData("ayuda", Command.Ayuda)]
    [InlineData("AYUDA", Command.Ayuda)]
    [InlineData("menu", Command.Ayuda)]
    [InlineData("agregar", Command.Agregar)]
    [InlineData("nueva propiedad", Command.Agregar)]
    [InlineData("listar", Command.Listar)]
    [InlineData("mis propiedades", Command.Listar)]
    [InlineData("resumen", Command.Resumen)]
    [InlineData("mis leads", Command.Resumen)]
    [InlineData("cualquier otra cosa que no sea comando", Command.Advisor)]
    public void Detect_RecognizesCanonicalCommandsAndAliases(string input, Command expected)
    {
        Detect(input).Command.Should().Be(expected);
    }

    [Fact]
    public void Detect_Pausar_ExtractsShortCode()
    {
        var d = Detect("pausar CASA-001");

        d.Command.Should().Be(Command.Pausar);
        d.ShortCode.Should().Be("CASA-001");
    }

    [Fact]
    public void Detect_DesactivarAlias_MapsToPausar()
    {
        var d = Detect("desactivar CASA-001");

        d.Command.Should().Be(Command.Pausar);
        d.ShortCode.Should().Be("CASA-001");
    }

    [Fact]
    public void Detect_Activar_ExtractsShortCode()
    {
        var d = Detect("activar depto-007");

        d.Command.Should().Be(Command.Activar);
        d.ShortCode.Should().Be("DEPTO-007");
    }

    [Fact]
    public void Detect_Publicidad_ParsesShortCodeBudgetAndDuration()
    {
        var d = Detect("publicidad CASA-001 500 14");

        d.Command.Should().Be(Command.Publicidad);
        d.ShortCode.Should().Be("CASA-001");
        d.BudgetMxn.Should().Be(500);
        d.DurationDays.Should().Be(14);
    }

    [Fact]
    public void Detect_Publicidad_NoBudget_FreePost()
    {
        var d = Detect("publicidad CASA-001");

        d.Command.Should().Be(Command.Publicidad);
        d.BudgetMxn.Should().BeNull();
        d.DurationDays.Should().Be(7); // default
    }

    [Fact]
    public void Detect_AccentsAndCase_Normalized()
    {
        Detect("AGRÉGAR").Command.Should().Be(Command.Agregar);
        Detect("Mis Propiedádes").Command.Should().Be(Command.Listar);
    }

    [Fact]
    public void Detect_EmptyOrNull_FallsThroughToAdvisor()
    {
        Detect(null).Command.Should().Be(Command.Advisor);
        Detect("").Command.Should().Be(Command.Advisor);
    }
}
