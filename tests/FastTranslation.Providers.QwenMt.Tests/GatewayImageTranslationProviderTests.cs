using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

public class GatewayImageTranslationProviderTests
{
    private static readonly byte[] Jpeg = { 1, 2, 3 };

    private static ImageTranslationRequest Request() => new(new byte[] { 9 }, "auto", "Chinese", string.Empty);

    private static ImageTranslationResult Ok(string url) => new(Jpeg, "auto", "Chinese", "qwen-mt-image-2.0", 20, url);

    private static ImageTranslationResult Denied(TranslationError error) => Ok(string.Empty) with { Error = error };

    private static Func<ImageTranslationRequest, CancellationToken, Task<ImageTranslationResult?>> Gateway(
        Func<ImageTranslationResult> respond) => (_, _) => Task.FromResult<ImageTranslationResult?>(respond());

    private sealed class StubProvider(Func<ImageTranslationResult> respond) : IImageTranslationProvider
    {
        public int Calls { get; private set; }

        public Task<ImageTranslationResult> TranslateImageAsync(
            ImageTranslationRequest request,
            CancellationToken cancellationToken)
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
        var own = new StubProvider(() => Ok("own.jpg"));
        using var provider = new GatewayImageTranslationProvider(Gateway(() => Ok("gateway.jpg")), own);

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal("gateway.jpg", result.RemoteUrl);
        Assert.Null(result.Notice);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task NotSignedIn_UsesTheOwnKey()
    {
        var own = new StubProvider(() => Ok("own.jpg"));
        using var provider = new GatewayImageTranslationProvider(
            (_, _) => Task.FromResult<ImageTranslationResult?>(null),
            own);

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal("own.jpg", result.RemoteUrl);
        Assert.Null(result.Notice);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task QuotaExhausted_FallsBackWithNotice()
    {
        var own = new StubProvider(() => Ok("own.jpg"));
        using var provider = new GatewayImageTranslationProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("本月额度已用完，这次用了你自己的 Key", result.Notice);
    }

    [Fact]
    public async Task Cancelled_DoesNotFallBack()
    {
        var own = new StubProvider(() => Ok("own.jpg"));
        using var provider = new GatewayImageTranslationProvider(Gateway(() => Denied(TranslationError.Cancelled())), own);

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task OwnKeyFailingToo_ReportsItsOwnError()
    {
        var own = new StubProvider(() => Denied(TranslationError.Authentication()));
        using var provider = new GatewayImageTranslationProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error!.Code);
        Assert.Null(result.Notice);
    }
}
