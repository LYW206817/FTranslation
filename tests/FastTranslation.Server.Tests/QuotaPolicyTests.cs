using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

public class QuotaPolicyTests
{
    [Fact]
    public void FreeTier_AllowsRequestWithinBothQuotas()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Free, usedMonthlyChars: 0, usedDailyChars: 0, requestedChars: 500);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
        Assert.Equal(Plans.FreeMonthlyChars - 500, decision.RemainingMonthly);
        Assert.Equal(Plans.FreeDailyChars - 500, decision.RemainingDaily);
        Assert.Equal(Plans.FreeDailyChars, decision.QuotaDaily);
    }

    [Fact]
    public void FreeTier_BlocksWhenMonthlyExhausted_EvenIfDailyHasRoom()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Free, Plans.FreeMonthlyChars, usedDailyChars: 0, requestedChars: 1);

        Assert.False(decision.Allowed);
        Assert.Equal("monthly_quota_exhausted", decision.Reason);
        Assert.Equal(0, decision.RemainingMonthly);
    }

    [Fact]
    public void FreeTier_BlocksWhenDailyExhausted_EvenIfMonthlyHasRoom()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Free, usedMonthlyChars: 0, usedDailyChars: Plans.FreeDailyChars, requestedChars: 1);

        Assert.False(decision.Allowed);
        Assert.Equal("daily_quota_exhausted", decision.Reason);
        Assert.Equal(0, decision.RemainingDaily);
    }

    [Fact]
    public void FreeTier_ReportsMonthlyFirstWhenBothAreExhausted()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Free, Plans.FreeMonthlyChars, Plans.FreeDailyChars, requestedChars: 1);

        Assert.False(decision.Allowed);
        Assert.Equal("monthly_quota_exhausted", decision.Reason);
    }

    [Theory]
    [InlineData(PlanTier.Light, Plans.LightMonthlyChars)]
    [InlineData(PlanTier.Standard, Plans.StandardMonthlyChars)]
    public void PaidTiers_HaveNoDailyCap(PlanTier tier, int expectedMonthly)
    {
        var decision = QuotaPolicy.Evaluate(tier, usedMonthlyChars: 0, usedDailyChars: 0, requestedChars: 1_000);

        Assert.True(decision.Allowed);
        Assert.Null(decision.QuotaDaily);
        Assert.Null(decision.RemainingDaily);
        Assert.Equal(expectedMonthly, decision.QuotaMonthly);
    }

    [Fact]
    public void PaidTier_BlocksAtMonthlyLimit()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Standard, Plans.StandardMonthlyChars, usedDailyChars: 0, requestedChars: 1);

        Assert.False(decision.Allowed);
        Assert.Equal("monthly_quota_exhausted", decision.Reason);
    }

    [Fact]
    public void ExactlyRemainingAmount_IsAllowed()
    {
        var decision = QuotaPolicy.Evaluate(PlanTier.Free, usedMonthlyChars: 9_500, usedDailyChars: 1_500, requestedChars: 500);

        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.RemainingMonthly);
        Assert.Equal(0, decision.RemainingDaily);
    }

    [Fact]
    public void NegativeRequest_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Evaluate(PlanTier.Free, 0, 0, -1));

    [Theory]
    [InlineData(PlanTier.Free, "free")]
    [InlineData(PlanTier.Light, "light")]
    [InlineData(PlanTier.Standard, "standard")]
    public void ToWire_MatchesClientContract(PlanTier tier, string expected)
        => Assert.Equal(expected, Plans.ToWire(tier));

    [Theory]
    [InlineData("free", PlanTier.Free)]
    [InlineData("light", PlanTier.Light)]
    [InlineData("standard", PlanTier.Standard)]
    public void TryParse_RoundTripsWireNames(string text, PlanTier expected)
    {
        Assert.True(Plans.TryParse(text, out var tier));
        Assert.Equal(expected, tier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Free")]
    [InlineData("premium")]
    [InlineData(null)]
    public void TryParse_RejectsUnknownNames(string? text)
    {
        Assert.False(Plans.TryParse(text, out var tier));
        Assert.Equal(PlanTier.Free, tier);
    }
}
