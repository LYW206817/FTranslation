using FastTranslation.Server.Domain;
using FastTranslation.Server.Orders;
using Xunit;

namespace FastTranslation.Server.Tests;

public class OrderPricingTests
{
    [Theory]
    [InlineData(PlanTier.Light, "monthly", 690, 30)]
    [InlineData(PlanTier.Light, "quarterly", 2_070, 90)]
    [InlineData(PlanTier.Standard, "monthly", 1_190, 30)]
    [InlineData(PlanTier.Standard, "quarterly", 3_570, 90)]
    [InlineData(PlanTier.Max, "monthly", 1_990, 30)]
    [InlineData(PlanTier.Max, "quarterly", 5_970, 90)]
    public void TryPrice_returns_the_server_side_price_and_period(
        PlanTier tier,
        string cycle,
        int expectedCents,
        int expectedDays)
    {
        Assert.True(OrderPricing.TryPrice(tier, cycle, out var cents));
        Assert.Equal(expectedCents, cents);
        Assert.Equal(expectedDays, OrderPricing.Days(cycle));
    }

    [Fact]
    public void TryPrice_rejects_the_free_tier()
    {
        Assert.False(OrderPricing.TryPrice(PlanTier.Free, OrderPricing.Monthly, out var cents));
        Assert.Equal(0, cents);
    }

    [Theory]
    [InlineData("yearly")]
    [InlineData("")]
    [InlineData(" monthlyy ")]
    public void TryPrice_rejects_unknown_cycles(string cycle)
    {
        Assert.False(OrderPricing.TryPrice(PlanTier.Standard, cycle, out var cents));
        Assert.Equal(0, cents);
    }

    [Theory]
    [InlineData(" Monthly ", OrderPricing.Monthly)]
    [InlineData("QUARTERLY", OrderPricing.Quarterly)]
    public void TryParseCycle_normalizes_case_and_whitespace(string input, string expected)
    {
        Assert.True(OrderPricing.TryParseCycle(input, out var cycle));
        Assert.Equal(expected, cycle);
    }

    [Fact]
    public void Days_falls_back_to_monthly_for_unknown_cycles()
    {
        // 历史订单可能带旧的 cycle 字符串；按 30 天开通，不接受新单的校验在 TryParseCycle 里。
        Assert.Equal(OrderPricing.MonthlyDays, OrderPricing.Days("yearly"));
    }
}
