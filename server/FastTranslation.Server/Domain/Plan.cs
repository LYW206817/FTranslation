namespace FastTranslation.Server.Domain;

/// <summary>存于 users.plan 的档位。传输名是与客户端之间的契约。</summary>
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

    /// <summary>免费档还额外限制每台设备每日用量（防滥用，设计 §7.4）。</summary>
    public const int FreeDailyChars = 2_000;

    public const int MaxDevicesPerAccount = 3;

    public static int MonthlyQuota(PlanTier tier) => tier switch
    {
        PlanTier.Light => LightMonthlyChars,
        PlanTier.Standard => StandardMonthlyChars,
        _ => FreeMonthlyChars,
    };

    /// <summary>Null 表示“无每日上限”——付费档只受月度额度约束。</summary>
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
