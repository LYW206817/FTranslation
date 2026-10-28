namespace FastTranslation.Server.Domain;

/// <summary>Tier stored in users.plan. The wire names are the contract with the client.</summary>
public enum PlanTier
{
    Free = 0,
    Light = 1,
    Standard = 2,
}

public static class Plans
{
    public const int FreeMonthlyChars = 10_000;
    public const int LightMonthlyChars = 50_000;
    public const int StandardMonthlyChars = 200_000;

    /// <summary>Free tier is additionally capped per device per day (anti-abuse, design §7.4).</summary>
    public const int FreeDailyChars = 2_000;

    public const int MaxDevicesPerAccount = 3;

    public static int MonthlyQuota(PlanTier tier) => tier switch
    {
        PlanTier.Light => LightMonthlyChars,
        PlanTier.Standard => StandardMonthlyChars,
        _ => FreeMonthlyChars,
    };

    /// <summary>Null means "no daily cap" — paid tiers are only bounded by the monthly quota.</summary>
    public static int? DailyQuota(PlanTier tier) => tier == PlanTier.Free ? FreeDailyChars : null;

    public static string ToWire(PlanTier tier) => tier switch
    {
        PlanTier.Light => "light",
        PlanTier.Standard => "standard",
        _ => "free",
    };

    public static bool TryParse(string? text, out PlanTier tier)
    {
        switch (text)
        {
            case "free":
                tier = PlanTier.Free;
                return true;
            case "light":
                tier = PlanTier.Light;
                return true;
            case "standard":
                tier = PlanTier.Standard;
                return true;
            default:
                tier = PlanTier.Free;
                return false;
        }
    }
}
