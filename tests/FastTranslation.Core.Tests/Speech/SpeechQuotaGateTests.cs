using FastTranslation.Core.Account;
using FastTranslation.Core.Speech;
using Xunit;

namespace FastTranslation.Core.Tests.Speech;

public sealed class SpeechQuotaGateTests
{
    /// <summary>只关心两个剩余额度，其余字段填免费档的真实上限。</summary>
    private static AccountUsage Usage(int monthlyLeft, int? dailyLeft) => new(
        UsedMonthlyChars: 0,
        UsedDailyChars: 0,
        QuotaMonthlyChars: 10_000,
        QuotaDailyChars: 2_000,
        RemainingMonthlyChars: monthlyLeft,
        RemainingDailyChars: dailyLeft,
        Month: "2026-11",
        Day: "2026-11-03");

    [Fact]
    public void A_full_free_tier_account_is_allowed_to_start()
    {
        Assert.Null(SpeechQuotaGate.Refusal(Usage(6_420, 300 + 1_200)));
    }

    [Fact]
    public void Daily_shortfall_refuses_even_when_the_month_still_has_room()
    {
        // 本轮要修的洞：今天 2000 字符用满、本月还剩六千多，旧预检只看月度就放行，
        // 服务端按日额度 429，用户只看到「点了没反应」。
        var refusal = SpeechQuotaGate.Refusal(Usage(6_420, 300));

        Assert.NotNull(refusal);
        Assert.Contains("今天", refusal);
    }

    [Fact]
    public void Monthly_shortfall_is_reported_before_the_daily_one()
    {
        var refusal = SpeechQuotaGate.Refusal(Usage(100, 100));

        Assert.Equal("语音额度不足,本月剩余不够 1 分钟,下月恢复", refusal);
    }

    [Fact]
    public void Tiers_without_a_daily_cap_are_not_refused_by_the_daily_window()
    {
        // 付费档没有每日上限，RemainingDailyChars 为 null，不能因为「没有日额度」被拒
        Assert.Null(SpeechQuotaGate.Refusal(Usage(50_000, null)));
    }

    [Fact]
    public void The_floor_is_one_minute_of_speech()
    {
        Assert.Equal(1_200, SpeechQuotaGate.MinStartChars);
        Assert.Null(SpeechQuotaGate.Refusal(Usage(1_200, 1_200)));
        Assert.NotNull(SpeechQuotaGate.Refusal(Usage(1_199, 5_000)));
    }

    [Fact]
    public void The_billed_minimum_is_five_seconds_of_speech()
    {
        // 值本身没逻辑，但它是跨进程的契约锚点：服务端 Gateway:Speech:MinSeconds 降到 5 秒(v98)，
        // 浮窗「已用 N 字符」用这两个常量折算。服务端改了而这里没跟上，显示用量会长期少记
        // （暂停→继续一次就少记 100 字符）。
        Assert.Equal(5, SpeechQuotaGate.MinBilledSeconds);
        Assert.Equal(100, SpeechQuotaGate.MinBilledSeconds * SpeechQuotaGate.CharsPerSecond);
    }
}
