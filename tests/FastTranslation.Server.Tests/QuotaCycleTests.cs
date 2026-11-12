using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>月度额度按账号自己的开通日结算，纯运算在这里钉住边界。</summary>
public sealed class QuotaCycleTests
{
    private static long Utc(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void Start_keeps_the_activation_day_through_the_first_month()
    {
        var anchor = Utc(2026, 11, 12);

        Assert.Equal(Utc(2026, 11, 12), QuotaCycle.Start(anchor, Utc(2026, 12, 11, 23)));
    }

    [Fact]
    public void Start_rolls_on_the_anniversary()
    {
        var anchor = Utc(2026, 11, 12);

        Assert.Equal(Utc(2026, 12, 12), QuotaCycle.Start(anchor, Utc(2026, 12, 12)));
    }

    [Fact]
    public void Start_truncates_the_anchor_to_utc_midnight()
    {
        // 开通时刻不影响周期边界：归零固定在北京时间 08:00。
        Assert.Equal("2026-11-12", QuotaCycle.Key(Utc(2026, 11, 12, 9), Utc(2026, 11, 12, 9)));
    }

    [Fact]
    public void Start_clamps_a_31st_anchor_to_the_last_day_of_february()
    {
        var anchor = Utc(2026, 1, 31);

        Assert.Equal(Utc(2026, 1, 31), QuotaCycle.Start(anchor, Utc(2026, 2, 27, 23)));
        Assert.Equal(Utc(2026, 2, 28), QuotaCycle.Start(anchor, Utc(2026, 2, 28)));
    }

    [Fact]
    public void Start_returns_to_the_original_day_after_a_short_month()
    {
        // 从原始锚点做 AddMonths，而不是从 2 月 28 日迭代，3 月才能回到 31 日。
        Assert.Equal(Utc(2026, 3, 31), QuotaCycle.Start(Utc(2026, 1, 31), Utc(2026, 3, 31)));
    }

    [Fact]
    public void Start_handles_a_leap_february()
    {
        Assert.Equal(Utc(2028, 2, 29), QuotaCycle.Start(Utc(2028, 1, 31), Utc(2028, 2, 29)));
    }

    [Fact]
    public void Key_is_the_period_start_date()
    {
        Assert.Equal("2026-12-12", QuotaCycle.Key(Utc(2026, 11, 12), Utc(2026, 12, 20)));
    }

    [Fact]
    public void Start_with_a_missing_anchor_falls_back_to_the_first_of_the_month()
    {
        // 未知账号的锚点是 0；周期退化为当月 1 日，用量本来也是 0。
        Assert.Equal("2026-11-01", QuotaCycle.Key(0, Utc(2026, 11, 3)));
    }
}
