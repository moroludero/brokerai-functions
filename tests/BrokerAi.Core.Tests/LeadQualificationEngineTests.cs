using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;
using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class LeadQualificationEngineTests
{
    private static Lead NewLead() => new() { Phone = "529981234567" };

    [Fact]
    public void Advance_NoGoal_AsksBuyOrRent()
    {
        var lead = NewLead();

        var output = LeadQualificationEngine.Advance(lead, LeadSteps.Greeting, "Ana Broker", isFirstMessage: true);

        output.NextStep.Should().Be(LeadSteps.ListingTypeStep);
        output.Reply.Should().Contain("comprar").And.Contain("rentar");
        output.ReadyForScoring.Should().BeFalse();
    }

    [Fact]
    public void Advance_GoalKnown_AsksBudgetNext()
    {
        var lead = NewLead();
        lead.Goal = LeadGoal.Comprar;

        var output = LeadQualificationEngine.Advance(lead, LeadSteps.ListingTypeStep, "Ana", false);

        output.NextStep.Should().Be(LeadSteps.Budget);
    }

    [Fact]
    public void Advance_SkipsAlreadyAnsweredQuestions()
    {
        // Everything known except visit_availability — must jump straight there,
        // never re-asking goal/budget/zone/type.
        var lead = NewLead();
        lead.Goal = LeadGoal.Comprar;
        lead.BudgetMax = 2_000_000;
        lead.Zone = Zones.Tulum;
        lead.PropertyType = "casa";

        var output = LeadQualificationEngine.Advance(lead, LeadSteps.Budget, "Ana", false);

        output.NextStep.Should().Be(LeadSteps.VisitAvailability);
    }

    [Fact]
    public void Advance_AllFieldsKnown_ReadyForScoring()
    {
        var lead = NewLead();
        lead.Goal = LeadGoal.Comprar;
        lead.BudgetMax = 2_000_000;
        lead.Zone = Zones.Tulum;
        lead.PropertyType = "casa";
        lead.VisitAvailability = "jueves";

        var output = LeadQualificationEngine.Advance(lead, LeadSteps.VisitAvailability, "Ana", false);

        output.ReadyForScoring.Should().BeTrue();
        output.NewStatus.Should().Be(LeadStatus.Qualified);
        output.NextStep.Should().Be(LeadSteps.Qualified);
    }

    [Fact]
    public void MergeExtraction_NeverOverwritesKnownValueWithNull()
    {
        var lead = NewLead();
        lead.Name = "Carlos";
        var extraction = new LeadExtraction
        {
            Intent = "specific",
            Extracted = new ExtractedFields { Name = null, Zone = Zones.Tulum },
        };

        LeadQualificationEngine.MergeExtraction(lead, extraction);

        lead.Name.Should().Be("Carlos");
        lead.Zone.Should().Be(Zones.Tulum);
    }

    [Fact]
    public void MergeExtraction_SingleBudgetValue_FillsBothMinAndMax()
    {
        var lead = NewLead();
        var extraction = new LeadExtraction
        {
            Extracted = new ExtractedFields { BudgetMax = 3_000_000 },
        };

        LeadQualificationEngine.MergeExtraction(lead, extraction);

        lead.BudgetMax.Should().Be(3_000_000);
        lead.BudgetMin.Should().Be(3_000_000);
    }

    [Fact]
    public void MergeExtraction_LeadGoal_MapsRentarCorrectly()
    {
        var lead = NewLead();
        var extraction = new LeadExtraction { LeadGoal = "rentar" };

        LeadQualificationEngine.MergeExtraction(lead, extraction);

        lead.Goal.Should().Be(LeadGoal.Rentar);
    }

    [Fact]
    public void ApplyQrProperty_PreFillsZoneAndType()
    {
        var lead = NewLead();
        var property = new Property
        {
            BrokerId = Guid.NewGuid(),
            Title = "Casa en Tulum",
            Zone = Zones.Tulum,
            Kind = PropertyKind.Casa,
            ListingKind = ListingType.Renta,
        };

        LeadQualificationEngine.ApplyQrProperty(lead, property);

        lead.Zone.Should().Be(Zones.Tulum);
        lead.PropertyType.Should().Be("casa");
        lead.Goal.Should().Be(LeadGoal.Rentar);
    }

    [Fact]
    public void OffTopicReply_IsDefinedAndNonEmpty()
    {
        LeadQualificationEngine.OffTopicReply.Should().NotBeNullOrWhiteSpace();
    }
}
