namespace FastTranslation.Server.Domain;

/// <summary>
/// 套餐实际解锁的能力。之所以从 <see cref="PlanTier"/> 拆出来，是因为“已付费”是一对状态：
/// 套餐档位加上订阅是否仍然有效。<c>plan_expires_at IS NULL</c> 表示
/// “授权没有到期时间”（管理员赠送 / 手动激活），这类授权永久有效——
/// 若反过来把 NULL 当作已过期，就会静默锁死每一个手动激活的账号。
/// </summary>
public static class Entitlement
{
    public static bool IsSubscriptionActive(PlanTier tier, long? expiresAtUnix, long now) =>
        tier != PlanTier.Free && (expiresAtUnix is not { } at || at > now);

    /// <summary>收藏到单词本是唯一的付费闸门（设计 §4.4）。</summary>
    public static bool AllowsNotebook(PlanTier tier, long? expiresAtUnix, long now) =>
        IsSubscriptionActive(tier, expiresAtUnix, now);

    /// <summary>
    /// 网关服务所有账号——免费账号拿到的是（更小的）额度，而不是被拒绝。
    /// 到期只会把额度降级，不会阻断翻译。
    /// </summary>
    public static PlanTier EffectiveTier(PlanTier tier, long? expiresAtUnix, long now) =>
        tier != PlanTier.Free && expiresAtUnix is { } at && at <= now ? PlanTier.Free : tier;
}
