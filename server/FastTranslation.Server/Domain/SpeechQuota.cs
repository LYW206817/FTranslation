namespace FastTranslation.Server.Domain;

/// <summary>
/// 语音额度的纯运算:把「剩余字符」折成本次会话能讲多少秒。
/// 单独抽出来做纯函数,理由与 <see cref="QuotaPolicy"/> 相同——额度算错直接等于烧钱。
/// </summary>
public static class SpeechQuota
{
    /// <summary>
    /// 本次会话允许的最长秒数 = min(剩余额度能讲的秒数, 单会话硬顶),且不低于 MinSeconds。
    /// 调用方必须先用 QuotaPolicy 预检过「最小会话」的额度,否则这里的下限会让实际
    /// 消耗略微超过剩余(= 欠费);有了那道预检,remainingChars ≥ MinSeconds×charsPerSecond。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">charsPerSecond ≤ 0 或 remainingChars &lt; 0。</exception>
    public static int SessionSeconds(int remainingChars, int charsPerSecond, int minSeconds, int maxSeconds)
    {
        if (charsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(charsPerSecond), "charsPerSecond must be positive");
        }

        if (remainingChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingChars), "remainingChars must not be negative");
        }

        var affordable = remainingChars / charsPerSecond;
        return Math.Max(minSeconds, Math.Min(affordable, maxSeconds));
    }
}
