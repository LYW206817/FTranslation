using FastTranslation.Server.Domain;

namespace FastTranslation.Server.Orders;

/// <summary>
/// 订阅订单的价格与周期。金额是服务端的唯一真相，客户端只传套餐和周期；
/// 改价时这里、桌面端套餐卡文案与 site/ 必须同批改。
/// </summary>
public static class OrderPricing
{
    public const string Monthly = "monthly";

    /// <summary>
    /// v141：停售年付，改卖季付。季付就是 3 倍月价、90 天，不再额外打折——
    /// 6.9 / 5.6 / 4.5 折是相对「88 万字符 10 元」的标价，不是年付那种「买 10 个月送 2 个月」。
    /// 历史订单里的 yearly 仍按库里的 days 开通，只是不再接受新单。
    /// </summary>
    public const string Quarterly = "quarterly";

    public const int MonthlyDays = 30;
    public const int QuarterlyDays = 90;

    /// <summary>分为单位，避免浮点；与套餐页的 ¥6.9 / ¥20.7 等展示价一致。</summary>
    private static readonly Dictionary<(PlanTier, string), int> PricesCents = new()
    {
        [(PlanTier.Light, Monthly)] = 690,
        [(PlanTier.Light, Quarterly)] = 2_070,
        [(PlanTier.Standard, Monthly)] = 1_190,
        [(PlanTier.Standard, Quarterly)] = 3_570,
        [(PlanTier.Max, Monthly)] = 1_990,
        [(PlanTier.Max, Quarterly)] = 5_970,
    };

    public static bool TryParseCycle(string? text, out string cycle)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Monthly:
                cycle = Monthly;
                return true;
            case Quarterly:
                cycle = Quarterly;
                return true;
            default:
                cycle = string.Empty;
                return false;
        }
    }

    public static int Days(string cycle) => cycle == Quarterly ? QuarterlyDays : MonthlyDays;

    public static bool TryPrice(PlanTier tier, string cycle, out int amountCents)
    {
        if (tier is PlanTier.Free || !TryParseCycle(cycle, out var normalized))
        {
            amountCents = 0;
            return false;
        }

        return PricesCents.TryGetValue((tier, normalized), out amountCents);
    }
}
