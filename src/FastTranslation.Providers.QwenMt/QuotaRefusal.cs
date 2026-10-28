using System.Text.Json;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// Recognises the developer gateway declining a request for money reasons. The gateway answers
/// these with 429/503 plus <c>{"error":"&lt;code&gt;"}</c> — the same status a genuine DashScope
/// throttle uses — so only the body can tell "your quota is spent, use your own key" apart from
/// "the server is busy, wait and retry".
/// </summary>
internal static class QuotaRefusal
{
    internal const string MonthlyQuota = "monthly_quota_exhausted";
    internal const string DailyQuota = "daily_quota_exhausted";
    internal const string BudgetExhausted = "budget_exhausted";

    /// <summary>Short cause for the user, or null when this body is not a refusal of that kind.</summary>
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
