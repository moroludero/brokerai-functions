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
    public void Zone_ValidInput_AdvancesToLocation(string input, string expectedZone)
    {
        var data = new IntakeData { ListingType = "venta" };
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Zone, data), input, null);

        result.NextState.Step.Should().Be(IntakeSteps.Location, "exact location comes right after the zone");
        result.NextState.Data.Zone.Should().Be(expectedZone);
    }

    [Fact]
    public void Location_NativeShare_StoresCoordinatesAndAdvances()
    {
        var data = new IntakeData { ListingType = "venta", Type = "casa", Zone = Zones.Tulum };
        var share = new PropertyIntakeStateMachine.LocationShare(21.161, -86.851, "Residencial X", "Av. Kabah 123");

        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Location, data), null, null, share);

        result.Error.Should().BeFalse();
        result.NextState.Step.Should().Be(IntakeSteps.Price);
        result.NextState.Data.Latitude.Should().Be(21.161);
        result.NextState.Data.Longitude.Should().Be(-86.851);
        result.NextState.Data.Address.Should().Be("Av. Kabah 123");
    }

    [Fact]
    public void Location_TypedAddress_StoresAddressAndAdvances()
    {
        var data = new IntakeData { ListingType = "venta", Type = "casa", Zone = Zones.Tulum };

        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Location, data), "Calle 10 Norte 245, Centro", null);

        result.NextState.Step.Should().Be(IntakeSteps.Price);
        result.NextState.Data.Address.Should().Be("Calle 10 Norte 245, Centro");
        result.NextState.Data.Latitude.Should().BeNull();
    }

    [Fact]
    public void Location_SinUbicacion_SkipsAndAdvances()
    {
        var data = new IntakeData { ListingType = "venta", Type = "casa", Zone = Zones.Tulum };

        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Location, data), "sin ubicación", null);

        result.NextState.Step.Should().Be(IntakeSteps.Price);
        result.NextState.Data.LocationDone.Should().BeTrue();
        result.NextState.Data.Latitude.Should().BeNull();
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

        // Each photo gets a silent 📸 reaction, NO text reply — forwarded
        // batches would otherwise spam one message per image.
        var r1 = PropertyIntakeStateMachine.Advance(state, "", "media-1");
        r1.Error.Should().BeFalse();
        r1.NextState.Step.Should().Be(IntakeSteps.Photo, "photo step loops until *listo*");
        r1.NextState.Data.MediaIds.Should().ContainSingle();
        r1.Reply.Should().BeNull("photos are acknowledged with a reaction, not text");
        r1.ReactWithEmoji.Should().Be("📸");

        var r2 = PropertyIntakeStateMachine.Advance(r1.NextState, "", "media-2");
        r2.NextState.Data.MediaIds.Should().HaveCount(2);
        r2.Reply.Should().BeNull();

        // The only text arrives on *listo*, with the total count.
        var r3 = PropertyIntakeStateMachine.Advance(r2.NextState, "listo", null);
        r3.NextState.Step.Should().Be(IntakeSteps.Description);
        r3.NextState.Data.MediaIds.Should().Equal("media-1", "media-2");
        r3.Reply.Should().Contain("2 fotos recibidas");
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
    public void Video_SinVideo_GoesToConfirmSummary()
    {
        var result = PropertyIntakeStateMachine.Advance(FilledUpToVideo(), "sin video", null);

        result.Done.Should().BeFalse("nothing saves without explicit confirmation");
        result.NextState.Step.Should().Be(IntakeSteps.Confirm);
        result.Reply.Should().Contain("Revisa los datos").And.Contain("confirmar");
    }

    [Fact]
    public void Video_HttpLink_GoesToConfirmWithUrl()
    {
        var result = PropertyIntakeStateMachine.Advance(FilledUpToVideo(), "https://youtu.be/xyz", null);

        result.NextState.Step.Should().Be(IntakeSteps.Confirm);
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
    public void Confirm_Confirmar_CompletesIntake()
    {
        var state = new BrokerIntakeState { Step = IntakeSteps.Confirm, Data = FilledUpToVideo().Data };
        state.Data.VideoDone = true;

        var result = PropertyIntakeStateMachine.Advance(state, "confirmar", null);

        result.Done.Should().BeTrue();
    }

    [Fact]
    public void Confirm_TargetedCorrection_FixesFieldAndReshowsSummary()
    {
        // The exact live scenario: broker typed 3 bathrooms, meant 1.
        var data = FilledUpToVideo().Data;
        data.Bathrooms = 3;
        data.VideoDone = true;
        var state = new BrokerIntakeState { Step = IntakeSteps.Confirm, Data = data };

        var result = PropertyIntakeStateMachine.Advance(state, "baños 1", null);

        result.Done.Should().BeFalse();
        result.NextState.Step.Should().Be(IntakeSteps.Confirm);
        result.NextState.Data.Bathrooms.Should().Be(1);
        result.Reply.Should().Contain("Baños actualizados").And.Contain("Baños: 1");
    }

    [Theory]
    [InlineData("precio 3000000")]
    [InlineData("zona playa del carmen")]
    [InlineData("recamaras 4")]
    public void Confirm_OtherCorrections_StayOnConfirm(string correction)
    {
        var data = FilledUpToVideo().Data;
        data.VideoDone = true;
        var state = new BrokerIntakeState { Step = IntakeSteps.Confirm, Data = data };

        var result = PropertyIntakeStateMachine.Advance(state, correction, null);

        result.Error.Should().BeFalse(correction);
        result.NextState.Step.Should().Be(IntakeSteps.Confirm);
    }

    [Fact]
    public void Cancelar_AtAnyStep_AbortsIntake()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.Bedrooms), "cancelar", null);

        result.Cancelled.Should().BeTrue();
        result.Done.Should().BeFalse();
    }

    [Fact]
    public void Atras_GoesBackOneStepAndReasksQuestion()
    {
        var data = new IntakeData { ListingType = "venta", Type = "casa", Zone = Zones.Tulum, LocationDone = true };
        var state = new BrokerIntakeState { Step = IntakeSteps.Price, Data = data };

        var result = PropertyIntakeStateMachine.Advance(state, "atras", null);

        result.NextState.Step.Should().Be(IntakeSteps.Location, "location sits between zone and price");
        result.Reply.Should().Contain("ubicación");
    }

    [Fact]
    public void Atras_AtFirstStep_StaysWithMessage()
    {
        var result = PropertyIntakeStateMachine.Advance(AtStep(IntakeSteps.ListingTypeStep), "atrás", null);

        result.NextState.Step.Should().Be(IntakeSteps.ListingTypeStep);
        result.Error.Should().BeTrue();
    }

    [Fact]
    public void FullConversation_EndToEnd_WithConfirmation_ReachesDone()
    {
        var state = new BrokerIntakeState();

        state = Step(state, "venta");
        state = Step(state, "casa");
        state = Step(state, "tulum");
        state = Step(state, "sin ubicación");
        state = Step(state, "2500000");
        state = Step(state, "3");
        state = Step(state, "2");
        state = Step(state, "sin foto");
        state = Step(state, "Casa hermosa con vista al mar, alberca privada");
        state = Step(state, "sin video");
        state.Step.Should().Be(IntakeSteps.Confirm, "summary screen comes before saving");

        var final = PropertyIntakeStateMachine.Advance(state, "confirmar", null);

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

    private static BrokerIntakeState FilledUpToVideo() => new()
    {
        Step = IntakeSteps.Video,
        Data = new IntakeData
        {
            ListingType = "venta",
            Type = "casa",
            Zone = Zones.Tulum,
            LocationDone = true,
            Price = 2_500_000,
            Bedrooms = 3,
            Bathrooms = 2,
            PhotosDone = true,
            Description = "Casa hermosa con vista al mar",
        },
    };
}
