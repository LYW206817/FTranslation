using System.Globalization;

namespace FastTranslation.Server.Domain;

/// <summary>
/// 月度额度周期：从账号开通日的 UTC 零点起，每满一个自然月为一段。
/// 锚点按 UTC 日取整，所以归零时刻固定在北京时间 08:00，和日额度同一个日界线；
/// 服务端不跟随用户机器的时区（换电脑不会多算一天）。
/// </summary>
public static class QuotaCycle
{
    private const long DaySeconds = 86_400;

    /// <summary>把任意 Unix 秒截到所在 UTC 日的零点。锚点与周期起点都用这个粒度。</summary>
    public static long StartOfUtcDay(long unix) => unix - (unix % DaySeconds);

    /// <summary>
    /// 包含 <paramref name="nowUnix"/> 的周期起点。始终从原始锚点做 AddMonths，
    /// 不拿上一个周期起点迭代——否则 1 月 31 日锚点会在 2 月被夹到 28 日后
    /// 永久停在 28 日，3 月回不到 31 日。
    /// </summary>
    public static long Start(long anchorUnix, long nowUnix)
    {
        var anchor = DateTimeOffset.FromUnixTimeSeconds(StartOfUtcDay(anchorUnix));
        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix);
        var months = (now.Year - anchor.Year) * 12 + now.Month - anchor.Month;
        var start = anchor.AddMonths(months);
        // 目标月的对应日还没到（如锚点 31 日、现在是 2 月 28 日之前），仍属于上一段。
        return (start > now ? anchor.AddMonths(months - 1) : start).ToUnixTimeSeconds();
    }

    /// <summary>周期键用起点日期（yyyy-MM-dd），直接作为 usage_monthly.month 的值。</summary>
    public static string Key(long anchorUnix, long nowUnix) =>
        DateTimeOffset.FromUnixTimeSeconds(Start(anchorUnix, nowUnix))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
