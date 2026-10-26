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
    public bool Succeeded => Error is null;
}

public interface ITranslationProvider : IDisposable
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
