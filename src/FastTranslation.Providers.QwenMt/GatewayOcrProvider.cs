using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// OCR 的组合 provider，与 <see cref="GatewayImageTranslationProvider"/> 同构：
/// 开发者网关不接收这次请求（没登录 / 没有令牌，回调返回 null）或额度已用完时，
/// 安静地改用用户自己的 Key；两条链路都失败时报用户自己 Key 的错。
/// </summary>
public sealed class GatewayOcrProvider : IImageOcrProvider
{
    private readonly Func<ImageOcrRequest, CancellationToken, Task<ImageOcrResult?>> _gateway;
    private readonly IImageOcrProvider _own;

    public GatewayOcrProvider(
        Func<ImageOcrRequest, CancellationToken, Task<ImageOcrResult?>> gateway,
        IImageOcrProvider own)
    {
        _gateway = gateway;
        _own = own;
    }

    public async Task<ImageOcrResult> RecognizeAsync(ImageOcrRequest request, CancellationToken cancellationToken)
    {
        var gateway = await _gateway(request, cancellationToken).ConfigureAwait(false);
        if (gateway is null) return await _own.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (gateway.Succeeded) return gateway;
        if (gateway.Error is { Code: TranslationErrorCode.Cancelled or TranslationErrorCode.ContentTooLong })
        {
            return gateway;
        }

        var own = await _own.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
        // 网关 200 但没认出字（Error 为 null、Text 为空）时没有可解释的原因，直接用自己的结果。
        return own.Succeeded && gateway.Error is { } error ? own with { Notice = Notice(error) } : own;
    }

    private static string? Notice(TranslationError error) => error.Code switch
    {
        TranslationErrorCode.QuotaExhausted => error.Message + "，这次用了你自己的 Key",
        TranslationErrorCode.RateLimited => "免费额度正在排队，这次用了你自己的 Key",
        _ => null,
    };

    public void Dispose() => _own.Dispose();
}
