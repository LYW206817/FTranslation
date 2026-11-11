namespace FastTranslation.Server.Domain;

/// <summary>
/// 存于 users.plan 的档位。传输名是与客户端之间的契约。
/// <para>
/// v124: 用户可见名称改成 Lite / Pro / Max，但 <c>light</c> / <c>standard</c> 两个
/// 传输名保持不动——users.plan 存的是整数，已有账号、激活码、客户端缓存和令牌里
/// 都是这两个名字；改显示名不需要一次全链路迁移。
/// </para>
/// </summary>
public enum PlanTier
{
    Free = 0,
    Light = 1,
    Standard = 2,
    Max = 3,
}

public static class Plans
{
    public const int FreeMonthlyChars = 10_000;

    // v124: 三档额度按用户定稿的 Lite 88 万 / Pro 188 万 / Max 388 万。
    // 改额度会改变各档的成本结构，定价核算见 .agents 设计文档；公开代码只留常量。
    public const int LightMonthlyChars = 880_000;
    public const int StandardMonthlyChars = 1_880_000;
    public const int MaxMonthlyChars = 3_880_000;

    /// <summary>免费档还额外限制每台设备每日用量（防滥用，设计 §7.4）。</summary>
    public const int FreeDailyChars = 2_000;

    public const int MaxDevicesPerAccount = 3;

    public static int MonthlyQuota(PlanTier tier) => tier switch
    {
        PlanTier.Light => LightMonthlyChars,
        PlanTier.Standard => StandardMonthlyChars,
        PlanTier.Max => MaxMonthlyChars,
        _ => FreeMonthlyChars,
    };

    /// <summary>Null 表示“无每日上限”——付费档只受月度额度约束。</summary>
    public static int? DailyQuota(PlanTier tier) => tier == PlanTier.Free ? FreeDailyChars : null;

    public static string ToWire(PlanTier tier) => tier switch
    {
        PlanTier.Light => "light",
        PlanTier.Standard => "standard",
        PlanTier.Max => "max",
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
            case "max":
                tier = PlanTier.Max;
                return true;
            default:
                tier = PlanTier.Free;
                return false;
        }
    }
}
