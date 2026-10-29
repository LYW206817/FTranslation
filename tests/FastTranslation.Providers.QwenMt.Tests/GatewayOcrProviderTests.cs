using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

public class GatewayOcrProviderTests
{
    private static ImageOcrRequest Request() => new(new byte[] { 9 });

    private static ImageOcrResult Ok(string text) => new(text, 12, string.Empty);

    private static ImageOcrResult Denied(TranslationError error) => Ok(string.Empty) with { Error = error };

    private static Func<ImageOcrRequest, CancellationToken, Task<ImageOcrResult?>> Gateway(
        Func<ImageOcrResult> respond) => (_, _) => Task.FromResult<ImageOcrResult?>(respond());

    private sealed class StubProvider(Func<ImageOcrResult> respond) : IImageOcrProvider
    {
        public int Calls { get; private set; }

        public Task<ImageOcrResult> RecognizeAsync(ImageOcrRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task SignedIn_ReturnsTheGatewayResult()
    {
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(Gateway(() => Ok("gateway")), own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal("gateway", result.Text);
        Assert.Null(result.Notice);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task NotSignedIn_UsesTheOwnKey()
    {
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(
            (_, _) => Task.FromResult<ImageOcrResult?>(null),
            own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal("own", result.Text);
        Assert.Null(result.Notice);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task QuotaExhausted_FallsBackWithNotice()
    {
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("own", result.Text);
        Assert.Equal("本月额度已用完，这次用了你自己的 Key", result.Notice);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task RateLimited_FallsBackWithItsOwnNotice()
    {
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.RateLimited, "请求过多"))),
            own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("免费额度正在排队，这次用了你自己的 Key", result.Notice);
    }

    [Fact]
    public async Task Cancelled_DoesNotFallBack()
    {
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(Gateway(() => Denied(TranslationError.Cancelled())), own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task ContentTooLong_DoesNotFallBack()
    {
        // 图太大是这次输入的问题，换一把 Key 也还是太大——重试只是再等一次超时。
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.ContentTooLong, "图片过大"))),
            own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.ContentTooLong, result.Error!.Code);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task OwnKeyFailingToo_ReportsItsOwnError()
    {
        var own = new StubProvider(() => Denied(TranslationError.Authentication()));
        using var provider = new GatewayOcrProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error!.Code);
        Assert.Null(result.Notice); // 自己的 Key 也不行时不要再解释网关那边发生了什么
    }

    [Fact]
    public async Task GatewaySucceededWithEmptyText_FallsBack()
    {
        // 网关 200 但没认出字，等于没成功：让自建 Key 的那条链路再试一次。
        var own = new StubProvider(() => Ok("own"));
        using var provider = new GatewayOcrProvider(Gateway(() => Ok(string.Empty)), own);

        var result = await provider.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal("own", result.Text);
        Assert.Equal(1, own.Calls);
    }
}
