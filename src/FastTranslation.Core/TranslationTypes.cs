namespace FastTranslation.Core;

public enum TranslationErrorCode
{
    None,
    Network,
    Authentication,
    RateLimited,
    ContentTooLong,
    InvalidResponse,
    ServerError,
    Cancelled,

    /// <summary>
    /// The gateway declined to serve this request because the account is out of quota (or the
    /// developer's free tier is not switched on). It is deliberately distinct from
    /// <see cref="RateLimited"/>: the composite provider treats it as "try the user's own key".
    /// </summary>
    QuotaExhausted,

    Unknown,
}

public sealed record TranslationError(TranslationErrorCode Code, string Message)
{
    public static TranslationError Cancelled() => new(TranslationErrorCode.Cancelled, "已取消");
    public static TranslationError Network(string detail) => new(TranslationErrorCode.Network, $"网络错误:{detail}");
    public static TranslationError Authentication() => new(TranslationErrorCode.Authentication, "API Key 无效或已过期");
    public static TranslationError RateLimited() => new(TranslationErrorCode.RateLimited, "请求过于频繁,请稍后再试");
    public static TranslationError ContentTooLong(int length, int max) => new(TranslationErrorCode.ContentTooLong, $"文本超长({length}>{max})");
    public static TranslationError InvalidResponse(string detail) => new(TranslationErrorCode.InvalidResponse, $"响应异常:{detail}");
}

public sealed record TranslationRequest(string Text, string SourceLanguage, string TargetLanguage, string Model)
{
    public const int MaxInputLength = 5000;
}

/// <summary>
/// What one request cost, as reported by the service that served it. Optional because a provider
/// that talks to a user's own key may not report usage, and because failures cost nothing.
/// </summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record TranslationResult(
    string RequestText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string Model,
    long ElapsedMs,
    bool FromCache)
{
    public TranslationError? Error { get; init; }

    /// <summary>Token accounting when the provider reported it; null otherwise.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// One extra sentence for the card, such as "the plan quota is spent, this one used your own
    /// key". Providers leave it null when the answer needs no explanation.
    /// </summary>
    public string? Notice { get; init; }

    public bool Succeeded => Error is null;
}

public interface ITranslationProvider : IDisposable
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
