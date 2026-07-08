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
    [InlineData("venderla", "venta")]
    [InlineData("es para renta", "renta")]
    [InlineData("quiero que se rente", "renta")]
    [InlineData("para arrendar", "renta")]
    [InlineData("alquilarla", "renta")]
    [InlineData("ambos", "ambos")]
    [InlineData("los dos", "ambos")]
    [InlineData("venta y renta", "ambos")]
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
    public void Photo_MultiplePhotosThenListo_CollectsAllAndAdvances()
    {
        var state = AtStep(IntakeSteps.Photo);

        var r1 = PropertyIntakeStateMachine.Advance(state, "", "media-1");
        r1.Error.Should().BeFalse();
        r1.NextState.Step.Should().Be(IntakeSteps.Photo, "photo step loops until *listo*");
        r1.NextState.Data.MediaIds.Should().ContainSingle();

        var r2 = PropertyIntakeStateMachine.Advance(r1.NextState, "", "media-2");
        r2.NextState.Data.MediaIds.Should().HaveCount(2);

        var r3 = PropertyIntakeStateMachine.Advance(r2.NextState, "listo", null);
        r3.NextState.Step.Should().Be(IntakeSteps.Description);
        r3.NextState.Data.MediaIds.Should().Equal("media-1", "media-2");
    }

    [Fact]
    public void Photo_ListoWithoutAnyPhoto_Rejected()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Photo), "listo", null);

        result.Error.Should().BeTrue("can't finish the photo step with zero photos unless saying *sin foto*");
    }

    [Fact]
    public void Photo_SinFoto_AdvancesToDescriptionWithNoImages()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Photo), "sin foto", null);

        result.NextState.Step.Should().Be(IntakeSteps.Description);
        result.NextState.Data.MediaIds.Should().BeEmpty();
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
