using FastTranslation.Core.Account;

namespace FastTranslation.Core.Speech;

/// <summary>
/// 语音会话开始前的额度预检。口径必须覆盖服务端会查的**每一个**桶——
/// 只比月度会让免费档的日额度用满后仍然放行，接着被服务端 429 挡回，
/// 而 429 在客户端只是「无法连接语音服务」，用户看到的就是「点了没反应」。
/// </summary>
public static class SpeechQuotaGate
{
    /// <summary>一秒音频折算的字符数，与服务端 Gateway:Speech:QuotaCharsPerSecond 同口径。
    /// v109: 20→50。20 字符/秒的 ASR 额度值 0.224 元/小时，ASR 实测/保守成本
    /// 0.31~0.86 元/小时，只覆盖 26%~73%；50 字符/秒值 0.56 元/小时，覆盖 65%~181%。
    /// 加上译文段后总覆盖约 70%~156%。仍低于市场语音翻译 0.077~1.7 元/分钟，
    /// 语音依然是补贴项，但没有 20 那么亏。</summary>
    public const int CharsPerSecond = 50;

    /// <summary>服务端最短计费秒数的镜像（Gateway:Speech:MinSeconds，v98 起为 5）：
    /// 不足它也按它记。浮窗的「已用 N 字符」必须走同一个数，否则显示值会比服务端少记。</summary>
    public const int MinBilledSeconds = 5;

    /// <summary>一分钟语音折算的字符数，与上面的 50 字符/秒记账口径一致。</summary>
    public const int OneMinuteChars = CharsPerSecond * 60;

    /// <summary>门槛取 30 秒而不是服务端的最小会话（5 秒 = 250 字符）：只剩五秒额度就允许开始，
    /// 用户讲两句就被掐掉，体验比直接拒绝更差。v109 从 1 分钟降到 30 秒：50 字符/秒下
    /// 一分钟要 3,000 字符，而免费档日额度只有 2,000，按 1 分钟会把免费用户全部挡在门外；
    /// 30 秒（1,500）仍能挡住「只剩几秒」的情况，免费档也能讲约 40 秒。</summary>
    public const int MinStartChars = CharsPerSecond * 30;

    /// <summary>可以开始返回 <see langword="null"/>；否则返回该显示给用户的中文原因。</summary>
    /// <param name="usage">服务端上报的额度快照。</param>
    /// <param name="minChars">开场门槛，测试用。</param>
    public static string? Refusal(AccountUsage usage, int minChars = MinStartChars)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.RemainingMonthlyChars < minChars)
            return "语音额度不足,本月剩余不够 30 秒,下月恢复";
        // 付费档没有每日上限，RemainingDailyChars 为 null 时不参与判定
        if (usage.RemainingDailyChars is { } dailyLeft && dailyLeft < minChars)
            return "语音额度不足,今天剩余不够 30 秒,明天恢复";
        return null;
    }

    /// <summary>
    /// 把剩余字符折成用户能读的时长。50 字符/秒下免费档一天只有 2,000 字符（40 秒），
    /// 继续只按分钟整除会显示「0 分钟」，所以不足一分钟时改显示秒。
    /// </summary>
    public static string DescribeDuration(int chars)
    {
        var seconds = Math.Max(0, chars) / CharsPerSecond;
        return seconds >= 60 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
    }

    /// <summary>
    /// 把网关 429 响应头 <c>X-FT-Quota-Reason</c> 的值翻成用户能懂的一句话。
    /// 月度和日额度必须分开说：只报「下月恢复」会让今天撞到日上限的人白等一个月。
    /// 未知值回落到通用额度用尽。
    /// </summary>
    public static string DescribeQuotaReason(string? reason) => reason switch
    {
        "monthly_quota_exhausted" => "本月语音额度已用完,下月恢复或升级套餐",
        "daily_quota_exhausted" => "今日语音额度已用完,明天恢复或升级套餐",
        _ => "语音额度已用完,请升级套餐",
    };
}
