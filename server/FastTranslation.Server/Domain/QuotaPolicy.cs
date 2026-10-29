namespace FastTranslation.Server.Domain;

/// <summary>
/// 纯额度运算。不依赖存储与 HTTP，因此可以直接做单元测试——
/// 额度 bug 是代价高昂的一类（多给烧钱，少给丢客户）。
/// </summary>
public static class QuotaPolicy
{
    public sealed record Decision(
        bool Allowed,
        string? Reason,
        int RemainingMonthly,
        int? RemainingDaily,
        int QuotaMonthly,
        int? QuotaDaily);

    public static Decision Evaluate(PlanTier tier, int usedMonthlyChars, int usedDailyChars, int requestedChars)
    {
        if (requestedChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedChars), "requestedChars must not be negative");
        }

        var quotaMonthly = Plans.MonthlyQuota(tier);
        var quotaDaily = Plans.DailyQuota(tier);

        var remainingMonthly = Math.Max(0, quotaMonthly - usedMonthlyChars);
        int? remainingDaily = quotaDaily is { } daily ? Math.Max(0, daily - usedDailyChars) : null;

        if (requestedChars > remainingMonthly)
        {
            return new Decision(false, "monthly_quota_exhausted", remainingMonthly, remainingDaily, quotaMonthly, quotaDaily);
        }

        if (remainingDaily is { } leftDaily && requestedChars > leftDaily)
        {
            return new Decision(false, "daily_quota_exhausted", remainingMonthly, remainingDaily, quotaMonthly, quotaDaily);
        }

        return new Decision(
            true,
            null,
            remainingMonthly - requestedChars,
            remainingDaily is { } left ? left - requestedChars : null,
            quotaMonthly,
            quotaDaily);
    }
}
