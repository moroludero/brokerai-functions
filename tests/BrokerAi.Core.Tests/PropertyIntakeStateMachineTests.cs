using BrokerAi.Core.Domain;
using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class PropertyIntakeStateMachineTests
{
    private static BrokerIntakeState AtStep(string step, IntakeData? data = null) =>
        new() { Step = step, Data = data ?? new IntakeData() };

    [Theory]
    [InlineData("venta", "venta")]
    [InlineData("Quiero vender", "venta")]
    [InlineData("es para renta", "renta")]
    [InlineData("ambos", "ambos")]
    [InlineData("los dos", "ambos")]
    public void ListingType_ValidInput_AdvancesToType(string input, string expectedListingType)
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.ListingTypeStep), input, null);

        result.Error.Should().BeFalse();
        result.NextState.Step.Should().Be(IntakeSteps.Type);
        result.NextState.Data.ListingType.Should().Be(expectedListingType);
    }

    [Fact]
    public void ListingType_InvalidInput_Reprompts()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.ListingTypeStep), "no se", null);

        result.Error.Should().BeTrue();
        result.NextState.Step.Should().Be(IntakeSteps.ListingTypeStep);
    }

    [Theory]
    [InlineData("casa", "casa")]
    [InlineData("depto", "depto")]
    [InlineData("departamento", "depto")]
    [InlineData("terreno", "terreno")]
    [InlineData("comercial", "comercial")]
    public void Type_ValidInput_AdvancesToZone(string input, string expected)
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Type), input, null);

        result.NextState.Step.Should().Be(IntakeSteps.Zone);
        result.NextState.Data.Type.Should().Be(expected);
    }

    [Theory]
    [InlineData("cancun centro", Zones.CancunCentro)]
    [InlineData("zona hotelera", Zones.ZonaHotelera)]
    [InlineData("playa del carmen", Zones.PlayaDelCarmen)]
    [InlineData("tulum", Zones.Tulum)]
    [InlineData("puerto morelos", Zones.PuertoMorelos)]
    public void Zone_ValidInput_AdvancesToPrice(string input, string expectedZone)
    {
        var data = new IntakeData { ListingType = "venta" };
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Zone, data), input, null);

        result.NextState.Step.Should().Be(IntakeSteps.Price);
        result.NextState.Data.Zone.Should().Be(expectedZone);
    }

    [Fact]
    public void Price_Renta_SkipsToBedroomsAndSetsRentPrice()
    {
        var data = new IntakeData { ListingType = "renta" };
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Price, data), "15000", null);

        result.NextState.Step.Should().Be(IntakeSteps.Bedrooms);
        result.NextState.Data.RentPrice.Should().Be(15000);
        result.NextState.Data.Price.Should().BeNull();
    }

    [Fact]
    public void Price_Ambos_AsksRentPriceNext()
    {
        var data = new IntakeData { ListingType = "ambos" };
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Price, data), "2500000", null);

        result.NextState.Step.Should().Be(IntakeSteps.RentPrice);
        result.NextState.Data.Price.Should().Be(2_500_000);
    }

    [Fact]
    public void Price_Venta_SkipsRentPriceEntirely()
    {
        var data = new IntakeData { ListingType = "venta" };
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Price, data), "2500000", null);

        result.NextState.Step.Should().Be(IntakeSteps.Bedrooms);
        result.NextState.Data.Price.Should().Be(2_500_000);
    }

    [Fact]
    public void Price_BelowMinimum_Rejected()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Price, new IntakeData { ListingType = "venta" }), "1000", null);

        result.Error.Should().BeTrue();
        result.NextState.Step.Should().Be(IntakeSteps.Price);
    }

    [Fact]
    public void Photo_WithMediaId_AdvancesToDescription()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Photo), "", "media-abc");

        result.NextState.Step.Should().Be(IntakeSteps.Description);
        result.NextState.Data.MediaId.Should().Be("media-abc");
    }

    [Fact]
    public void Photo_SinFoto_AdvancesToDescriptionWithNullImage()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Photo), "sin foto", null);

        result.NextState.Step.Should().Be(IntakeSteps.Description);
        result.NextState.Data.ImageUrl.Should().BeNull();
    }

    [Fact]
    public void Photo_NoMediaNoSkipText_Rejected()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Photo), "aqui va", null);

        result.Error.Should().BeTrue();
    }

    [Fact]
    public void Description_TooShort_Rejected()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Description), "linda", null);

        result.Error.Should().BeTrue();
    }

    [Fact]
    public void Video_SinVideo_CompletesIntake()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Video), "sin video", null);

        result.Done.Should().BeTrue();
        result.NextState.Step.Should().Be(IntakeSteps.Done);
        result.NextState.Data.VideoUrl.Should().BeNull();
    }

    [Fact]
    public void Video_HttpLink_CompletesIntakeWithUrl()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Video), "https://youtu.be/xyz", null);

        result.Done.Should().BeTrue();
        result.NextState.Data.VideoUrl.Should().Be("https://youtu.be/xyz");
    }

    [Fact]
    public void Video_NonHttpNonSkip_Rejected()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Video), "vimeo.com/xyz", null);

        result.Error.Should().BeTrue();
        result.Done.Should().BeFalse();
    }

    [Fact]
    public void FullConversation_EndToEnd_ReachesDone()
    {
        var state = new BrokerIntakeState();

        state = Step(state, "venta");
        state = Step(state, "casa");
        state = Step(state, "tulum");
        state = Step(state, "2500000");
        state = Step(state, "3");
        state = Step(state, "2");
        state = Step(state, "sin foto");
        state = Step(state, "Casa hermosa con vista al mar, alberca privada");
        var final = PropertyIntakeStateMachine.Advance(state, "sin video", null);

        final.Done.Should().BeTrue();
        final.NextState.Data.ListingType.Should().Be("venta");
        final.NextState.Data.Type.Should().Be("casa");
        final.NextState.Data.Zone.Should().Be(Zones.Tulum);
        final.NextState.Data.Price.Should().Be(2_500_000);
        final.NextState.Data.Bedrooms.Should().Be(3);
        final.NextState.Data.Bathrooms.Should().Be(2);
    }

    private static BrokerIntakeState Step(BrokerIntakeState state, string input) =>
        PropertyIntakeStateMachine.Advance(state, input, null).NextState;
}
