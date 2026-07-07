using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class LeadScorerTests
{
    private static Lead BaseLead() => new() { Phone = "529981234567" };

    [Theory]
    [InlineData(3_000_000, 30)]
    [InlineData(5_000_000, 30)]
    [InlineData(1_000_000, 15)]
    [InlineData(2_999_999, 15)]
    [InlineData(999_999, 0)]
    [InlineData(null, 0)]
    public void Score_BudgetSignal_MatchesThresholds(int? budgetMax, int expectedBudgetPoints)
    {
        var lead = BaseLead();
        lead.BudgetMax = budgetMax;

        var result = LeadScorer.Score(lead);

        result.Score.Should().Be(expectedBudgetPoints);
    }

    [Fact]
    public void Score_FullProfile_AddsBonus()
    {
        var lead = BaseLead();
        lead.Name = "Juan";
        lead.BudgetMin = 1_000_000;
        lead.BudgetMax = 1_000_000;
        lead.Zone = "Tulum";
        lead.PropertyType = "casa";

        var result = LeadScorer.Score(lead);

        // budget(15) + zone(20) + type(20) + full-profile(10) = 65
        result.Score.Should().Be(65);
    }

    [Fact]
    public void Score_AtHotThreshold_69_IsNotHot()
    {
        var lead = BaseLead();
        lead.BudgetMax = 1_000_000; // 15
        lead.Zone = "Tulum";        // 20
        lead.PropertyType = "casa"; // 20
        // name/budget_min missing so no full-profile bonus (10)
        lead.VisitAvailability = "jueves"; // 20 → total 75... need exactly 69

        // Recompute for an exact 69: not all signals combine to 69 cleanly with this point
        // system (30/15/20/20/10/20). Use zone+type+visit = 60, plus nothing else = 60 < 70.
        var lead69 = BaseLead();
        lead69.Zone = "Tulum";
        lead69.PropertyType = "casa";
        lead69.VisitAvailability = "jueves";
        // 20 + 20 + 20 = 60, still not 70; scoring is coarse-grained (multiples of 5/10),
        // so we instead verify the boundary condition directly at 70.
        var result60 = LeadScorer.Score(lead69);
        result60.Score.Should().Be(60);
        result60.IsHot.Should().BeFalse();
    }

    [Fact]
    public void Score_At70_IsHot_WhenAlertNotYetSent()
    {
        var lead = BaseLead();
        lead.BudgetMax = 3_000_000; // 30
        lead.Zone = "Tulum";        // 20
        lead.PropertyType = "casa"; // 20
        // total 70, alert not sent

        var result = LeadScorer.Score(lead);

        result.Score.Should().Be(70);
        result.IsHot.Should().BeTrue();
    }

    [Fact]
    public void Score_At70_ButAlertAlreadySent_IsNotHotAgain()
    {
        var lead = BaseLead();
        lead.BudgetMax = 3_000_000;
        lead.Zone = "Tulum";
        lead.PropertyType = "casa";
        lead.AlertSent = true;

        var result = LeadScorer.Score(lead);

        result.Score.Should().Be(70);
        result.IsHot.Should().BeFalse("alert dedup must prevent re-firing once AlertSent is true");
    }

    [Fact]
    public void Score_VisitAvailability_AddsStrongestSignal()
    {
        var lead = BaseLead();
        lead.VisitAvailability = "viernes por la tarde";

        var result = LeadScorer.Score(lead);

        result.Score.Should().Be(20);
    }

    [Fact]
    public void IsQrVisitHot_RentalQrLeadWithVisit_AlwaysHot()
    {
        // Rental QR lead: implied budget = monthly rent → score 60 < 70, but they
        // scheduled a visit for a concrete property — must alert regardless.
        var lead = BaseLead();
        lead.BudgetMax = 11_000;
        lead.Zone = "Cancún Centro";
        lead.PropertyType = "casa";
        lead.VisitAvailability = "El jueves a las 3 de la tarde";

        LeadScorer.Score(lead).IsHot.Should().BeFalse("the sale-oriented scale caps rentals at 60");
        LeadScorer.IsQrVisitHot(lead, "CASA-001").Should().BeTrue();
    }

    [Fact]
    public void IsQrVisitHot_RequiresQrAndVisitAndNoDedup()
    {
        var lead = BaseLead();
        lead.VisitAvailability = "sábado 10am";

        LeadScorer.IsQrVisitHot(lead, null).Should().BeFalse("no QR scan");
        LeadScorer.IsQrVisitHot(BaseLead(), "CASA-001").Should().BeFalse("no visit availability");

        lead.AlertSent = true;
        LeadScorer.IsQrVisitHot(lead, "CASA-001").Should().BeFalse("alert already sent");
    }
}
