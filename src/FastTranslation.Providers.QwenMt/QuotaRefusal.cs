using System.Text.Json;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// 识别「这次拒绝是钱的问题」而不是「服务端忙」。两种来源形状不同：
/// <list type="bullet">
/// <item>自建网关用 429/503 加 <c>{"error":"&lt;code&gt;"}</c>（error 是字符串）；</item>
/// <item>DashScope 自己用 429 加 <c>{"error":{"code":"insufficient_quota",…}}</c>（error 是对象）。</item>
/// </list>
/// 两种状态码都与真实限流相同，所以只能靠响应体区分「额度用完，别再重试」与「服务端繁忙，等一下重试」。
/// </summary>
internal static class QuotaRefusal
{
    internal const string MonthlyQuota = "monthly_quota_exhausted";
    internal const string DailyQuota = "daily_quota_exhausted";
    internal const string BudgetExhausted = "budget_exhausted";

    /// <summary>
    /// DashScope 账户余额或免费额度耗尽的错误码（v75）。
    /// <para>
    /// 之前只认上面三个自建网关的码，于是直连时这条错误被当成 <see cref="TranslationErrorCode.RateLimited"/>：
    /// 一次真实的 429 会被无意义地再重试一遍，用户看到的还是「在排队」——
    /// 100 页文档实测里 658 段有 417 段「失败」，真正原因是账户额度已耗尽，而不是请求太快。
    /// </para>
    /// </summary>
    internal const string InsufficientQuota = "insufficient_quota";

    /// <summary>给用户看的简短原因；响应体不属于这类拒绝时返回 null。</summary>
    internal static string? Describe(string? body)
    {
        var code = ReadErrorCode(body);
        return code switch
        {
            MonthlyQuota => "本月额度已用完",
            DailyQuota => "今日额度已用完",
            BudgetExhausted => "免费额度暂时用完",
            InsufficientQuota => "API Key 的额度或余额不足",
            _ => null,
        };
    }

    /// <summary>
    /// 读错误码，兼容 <c>{"error":"code"}</c> 与 <c>{"error":{"code":"…"}}</c> 两种形状。
    /// 只有 <c>code</c> 是契约；<c>message</c>/<c>type</c>/<c>param</c> 不参与判定。
    /// </summary>
    internal static string? ReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("error", out var error)) return null;
            return error.ValueKind switch
            {
                JsonValueKind.String => error.GetString(),
                JsonValueKind.Object when error.TryGetProperty("code", out var code)
                                          && code.ValueKind == JsonValueKind.String => code.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
