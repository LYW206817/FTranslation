using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// 图片翻译的组合 provider，与 <see cref="GatewayTranslationProvider"/> 同构：
/// 开发者网关不接收这次请求（没登录 / 没有令牌，回调返回 null）或额度已用完时，
/// 安静地改用用户自己的 Key；两条链路都失败时报用户自己 Key 的错。
/// </summary>
public sealed class GatewayImageTranslationProvider : IImageTranslationProvider
{
    private readonly Func<ImageTranslationRequest, CancellationToken, Task<ImageTranslationResult?>> _gateway;
    private readonly IImageTranslationProvider _own;

    public GatewayImageTranslationProvider(
        Func<ImageTranslationRequest, CancellationToken, Task<ImageTranslationResult?>> gateway,
        IImageTranslationProvider own)
    {
        _gateway = gateway;
        _own = own;
    }

    public async Task<ImageTranslationResult> TranslateImageAsync(
        ImageTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var gateway = await _gateway(request, cancellationToken).ConfigureAwait(false);
        if (gateway is null) return await _own.TranslateImageAsync(request, cancellationToken).ConfigureAwait(false);
        if (gateway.Succeeded) return gateway;
        if (gateway.Error is { Code: TranslationErrorCode.Cancelled or TranslationErrorCode.ContentTooLong })
        {
            return gateway;
        }

        var own = await _own.TranslateImageAsync(request, cancellationToken).ConfigureAwait(false);
        return own.Succeeded ? own with { Notice = Notice(gateway.Error!) } : own;
    }

    private static string? Notice(TranslationError error) => error.Code switch
    {
        TranslationErrorCode.QuotaExhausted => error.Message + "，这次用了你自己的 Key",
        TranslationErrorCode.RateLimited => "免费额度正在排队，这次用了你自己的 Key",
        _ => null,
    };

    public void Dispose() => _own.Dispose();
}
