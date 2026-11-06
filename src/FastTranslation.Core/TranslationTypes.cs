using FastTranslation.Core.Lexicon;

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
    /// 网关因账户额度用尽（或开发者的免费档未开通）拒绝服务这次请求。它刻意与
    /// <see cref="RateLimited"/> 区分开：组合 provider 会把它当作「改用用户自己的 Key」。
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
/// 一次请求的用量，由实际提供服务的那个服务上报。之所以可选：直连用户自己 Key 的 provider
/// 可能不上报用量，而且失败的请求不产生费用。
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

    /// <summary>provider 上报时的 token 用量统计，否则为 null。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// 结果卡上多显示的一句说明，例如「套餐额度已用完，这次用了你自己的 Key」。
    /// 不需要解释时 provider 留 null。
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>
    /// 本地词库命中时的单词卡。只有单英文词且目标语为中文时才会填充；
    /// 在线翻译结果为 null，界面据此在「一行译文」和「词典块」之间切换。
    /// </summary>
    public DictionaryEntry? Dictionary { get; init; }

    public bool Succeeded => Error is null;
}

public interface ITranslationProvider : IDisposable
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
