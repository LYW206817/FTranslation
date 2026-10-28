using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

/// <summary>
/// 组合 provider：先用开发者网关（内置免费/付费额度），网关拒绝时安静地改用用户自己的 Key。
/// 令牌会过期、没登录就没有令牌，这两种情况都交给 <paramref name="gateway"/> 回调判断：
/// 返回 null 表示"这次直接用用户自己的 Key"，不打扰用户。
/// </summary>
public sealed class GatewayTranslationProvider : ITranslationProvider
{
    private readonly Func<TranslationRequest, CancellationToken, Task<TranslationResult?>> _gateway;
    private readonly ITranslationProvider _own;

    public GatewayTranslationProvider(
        Func<TranslationRequest, CancellationToken, Task<TranslationResult?>> gateway,
        ITranslationProvider own)
    {
        _gateway = gateway;
        _own = own;
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var gateway = await _gateway(request, cancellationToken).ConfigureAwait(false);
        if (gateway is null) return await _own.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (gateway.Succeeded) return gateway;

        // 取消和超长是请求本身的问题，换成用户自己的 Key 也是同样的结果，不重试。
        if (gateway.Error is { Code: TranslationErrorCode.Cancelled or TranslationErrorCode.ContentTooLong })
        {
            return gateway;
        }

        var own = await _own.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!own.Succeeded)
        {
            // 两条链路都失败时报用户自己 Key 的错，那是他能动手改的那个。
            return own;
        }

        return own with { Notice = DescribeGatewayRefusal(gateway.Error!) };
    }

    private static string? DescribeGatewayRefusal(TranslationError error) => error.Code switch
    {
        TranslationErrorCode.QuotaExhausted => error.Message + "，这次用了你自己的 Key",
        TranslationErrorCode.RateLimited => "免费额度正在排队，这次用了你自己的 Key",
        // 网关的网络/服务端故障不值得打扰用户，回落本身已经说明问题。
        _ => null,
    };

    public void Dispose()
    {
        _own.Dispose();
    }
}
