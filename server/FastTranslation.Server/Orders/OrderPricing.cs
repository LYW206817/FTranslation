using FastTranslation.Server.Domain;

namespace FastTranslation.Server.Orders;

/// <summary>
/// 订阅订单的价格与周期。金额是服务端的唯一真相，客户端只传套餐和周期；
/// 改价时这里、桌面端套餐卡文案与 site/ 必须同批改。
/// </summary>
public static class OrderPricing
{
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";

    public const int MonthlyDays = 30;
    public const int YearlyDays = 365;

    /// <summary>分为单位，避免浮点；与套餐页的 ¥6.9 / ¥69 等展示价一致。</summary>
    private static readonly Dictionary<(PlanTier, string), int> PricesCents = new()
    {
        [(PlanTier.Light, Monthly)] = 690,
        [(PlanTier.Light, Yearly)] = 6_900,
        [(PlanTier.Standard, Monthly)] = 1_190,
        [(PlanTier.Standard, Yearly)] = 11_900,
        [(PlanTier.Max, Monthly)] = 1_990,
        [(PlanTier.Max, Yearly)] = 19_900,
    };

    public static bool TryParseCycle(string? text, out string cycle)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Monthly:
                cycle = Monthly;
                return true;
            case Yearly:
                cycle = Yearly;
                return true;
            default:
                cycle = string.Empty;
                return false;
        }
    }

    public static int Days(string cycle) => cycle == Yearly ? YearlyDays : MonthlyDays;

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
