using System.Text.Json;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// 识别开发者网关因费用原因拒绝请求。网关用 429/503 加 <c>{"error":"&lt;code&gt;"}</c> 作答——
/// 与 DashScope 真实限流用的状态码相同——所以只能靠响应体区分
/// 「额度用完，改用你自己的 Key」与「服务端繁忙，等一下重试」。
/// </summary>
internal static class QuotaRefusal
{
    internal const string MonthlyQuota = "monthly_quota_exhausted";
    internal const string DailyQuota = "daily_quota_exhausted";
    internal const string BudgetExhausted = "budget_exhausted";

    /// <summary>给用户看的简短原因；响应体不属于这类拒绝时返回 null。</summary>
    internal static string? Describe(string? body)
    {
        var code = ReadErrorCode(body);
        return code switch
        {
            MonthlyQuota => "本月额度已用完",
            DailyQuota => "今日额度已用完",
            BudgetExhausted => "免费额度暂时用完",
            _ => null,
        };
    }

    internal static string? ReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
