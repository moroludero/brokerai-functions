using BrokerAi.Core.Domain;
using BrokerAi.Core.Services;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class PlanGateTests
{
    [Theory]
    [InlineData(PlanTier.Basico, 149, true)]
    [InlineData(PlanTier.Basico, 150, false)]
    [InlineData(PlanTier.Pro, 399, true)]
    [InlineData(PlanTier.Pro, 400, false)]
    [InlineData(PlanTier.Agencia, 100_000, true)]
    public void NewLead_RespectsPlanLimits(PlanTier plan, int leadsThisMonth, bool expectedAllowed)
    {
        var result = PlanGate.Check(new PlanGate.Request("new_lead", plan, LeadsThisMonth: leadsThisMonth));

        result.Allowed.Should().Be(expectedAllowed);
    }

    [Theory]
    [InlineData(PlanTier.Basico, 19, true)]
    [InlineData(PlanTier.Basico, 20, false)]
    [InlineData(PlanTier.Pro, 49, true)]
    [InlineData(PlanTier.Pro, 50, false)]
    public void NewProperty_RespectsPlanLimits(PlanTier plan, int propertiesActive, bool expectedAllowed)
    {
        var result = PlanGate.Check(new PlanGate.Request("new_property", plan, PropertiesActive: propertiesActive));

        result.Allowed.Should().Be(expectedAllowed);
    }

    [Theory]
    [InlineData(PlanTier.Basico, false)]
    [InlineData(PlanTier.Pro, true)]
    [InlineData(PlanTier.Agencia, true)]
    public void FacebookPost_GatedByPlan(PlanTier plan, bool expectedAllowed)
    {
        var result = PlanGate.Check(new PlanGate.Request("facebook_post", plan));

        result.Allowed.Should().Be(expectedAllowed);
    }

    [Fact]
    public void FacebookAd_BlockedForNonAgenciaEvenWithBudget()
    {
        var result = PlanGate.Check(new PlanGate.Request(
            "facebook_ad", PlanTier.Pro, MonthlyAdBudget: 5000, RequestedBudget: 500));

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void FacebookAd_Agencia_NoAdBudget_Blocked()
    {
        var result = PlanGate.Check(new PlanGate.Request(
            "facebook_ad", PlanTier.Agencia, MonthlyAdBudget: 0, RequestedBudget: 500));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("no_ad_budget");
    }

    [Fact]
    public void FacebookAd_Agencia_InsufficientBudget_Blocked()
    {
        var result = PlanGate.Check(new PlanGate.Request(
            "facebook_ad", PlanTier.Agencia, MonthlyAdBudget: 1000, AdSpentThisMonth: 800, RequestedBudget: 500));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("insufficient_ad_budget");
        result.AdAvailableMxn.Should().Be(200);
    }

    [Fact]
    public void FacebookAd_Agencia_SufficientBudget_Allowed()
    {
        var result = PlanGate.Check(new PlanGate.Request(
            "facebook_ad", PlanTier.Agencia, MonthlyAdBudget: 1000, AdSpentThisMonth: 200, RequestedBudget: 500));

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void UnknownFeature_Throws()
    {
        var act = () => PlanGate.Check(new PlanGate.Request("not_a_real_feature", PlanTier.Basico));

        act.Should().Throw<ArgumentException>("unknown feature names must no longer silently pass, unlike the old n8n node");
    }
}
