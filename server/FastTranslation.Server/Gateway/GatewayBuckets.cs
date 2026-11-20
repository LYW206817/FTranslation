namespace FastTranslation.Server.Gateway;

/// <summary>
/// 日预算桶的名字。桶名同时是 <c>usage_events.source</c>：预留、结算、当日已花费、
/// 管理页与监控告警五处必须用同一个字符串，否则某处会悄悄看到 0 成本而永不告警。
/// </summary>
public static class GatewayBuckets
{
    /// <summary>免费账号（含订阅已到期）的文本 / 图片 / OCR。</summary>
    public const string Free = "gateway";

    /// <summary>有效档位非免费的账号，与 <see cref="Free"/> 分桶，互不连坐。</summary>
    public const string Paid = "gateway_paid";

    /// <summary>语音，独立成桶：烧完只停语音，不连累文本翻译。</summary>
    public const string Speech = "speech";

    /// <summary>按展示与告警顺序返回每个桶的来源名和当日预算；预算 0 表示不限。</summary>
    public static IReadOnlyList<(string Source, long BudgetMicros)> All(GatewayOptions options) =>
    [
        (Free, options.DailyBudgetMicros),
        (Paid, options.PaidDailyBudgetMicros),
        (Speech, options.Speech.DailyBudgetMicros),
    ];
}
