namespace FastTranslation.Server.Domain;

/// <summary>
/// Pure quota arithmetic. Kept free of storage and HTTP so it can be unit tested directly —
/// quota bugs are the expensive kind (over-serving costs money, under-serving loses customers).
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
