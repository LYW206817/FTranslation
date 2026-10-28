namespace FastTranslation.Server.Domain;

/// <summary>
/// What a plan actually unlocks. Split out from <see cref="PlanTier"/> because "paid" is a pair:
/// the tier plus whether the subscription is still alive. <c>plan_expires_at IS NULL</c> means
/// "granted without an end date" (admin gift / manual activation), which stays active forever —
/// the alternative (treating NULL as expired) would silently lock out every hand-activated account.
/// </summary>
public static class Entitlement
{
    public static bool IsSubscriptionActive(PlanTier tier, long? expiresAtUnix, long now) =>
        tier != PlanTier.Free && (expiresAtUnix is not { } at || at > now);

    /// <summary>Saving to the notebook is the single paid gate (design §4.4).</summary>
    public static bool AllowsNotebook(PlanTier tier, long? expiresAtUnix, long now) =>
        IsSubscriptionActive(tier, expiresAtUnix, now);

    /// <summary>
    /// The gateway serves every account — a free account gets its (smaller) quota, not a refusal.
    /// Expiry only downgrades the quota, it does not block translation.
    /// </summary>
    public static PlanTier EffectiveTier(PlanTier tier, long? expiresAtUnix, long now) =>
        tier != PlanTier.Free && expiresAtUnix is { } at && at <= now ? PlanTier.Free : tier;
}
