using FastTranslation.Core.Account;

namespace FastTranslation.Core.Speech;

/// <summary>
/// 语音会话开始前的额度预检。口径必须覆盖服务端会查的**每一个**桶——
/// 只比月度会让免费档的日额度用满后仍然放行，接着被服务端 429 挡回，
/// 而 429 在客户端只是「无法连接语音服务」，用户看到的就是「点了没反应」。
/// </summary>
public static class SpeechQuotaGate
{
    /// <summary>一秒音频折算的字符数，与服务端 Gateway:Speech:QuotaCharsPerSecond 同口径。</summary>
    public const int CharsPerSecond = 20;

    /// <summary>服务端最短计费秒数的镜像（Gateway:Speech:MinSeconds，v98 起为 5）：
    /// 不足它也按它记。浮窗的「已用 N 字符」必须走同一个数，否则显示值会比服务端少记。</summary>
    public const int MinBilledSeconds = 5;

    /// <summary>一分钟语音折算的字符数，与上面的 20 字符/秒记账口径一致。</summary>
    public const int OneMinuteChars = CharsPerSecond * 60;

    /// <summary>门槛取一分钟而不是服务端的最小会话（5 秒 = 100 字符）：只剩五秒额度就允许开始，
    /// 用户讲两句就被掐掉，体验比直接拒绝更差。客户端比服务端严不会造成误拒，反之会。</summary>
    public const int MinStartChars = OneMinuteChars;

    /// <summary>可以开始返回 <see langword="null"/>；否则返回该显示给用户的中文原因。</summary>
    /// <param name="usage">服务端上报的额度快照。</param>
    /// <param name="minChars">开场门槛，测试用。</param>
    public static string? Refusal(AccountUsage usage, int minChars = MinStartChars)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.RemainingMonthlyChars < minChars)
            return "语音额度不足,本月剩余不够 1 分钟,下月恢复";
        // 付费档没有每日上限，RemainingDailyChars 为 null 时不参与判定
        if (usage.RemainingDailyChars is { } dailyLeft && dailyLeft < minChars)
            return "语音额度不足,今天剩余不够 1 分钟,明天恢复";
        return null;
    }
}
