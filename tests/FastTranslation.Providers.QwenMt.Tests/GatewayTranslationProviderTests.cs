using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

public class GatewayTranslationProviderTests
{
    private static TranslationRequest Request() => new("hello", "auto", "Chinese", "qwen-mt-flash");

    private static TranslationResult Ok(string text) => new("hello", text, "auto", "Chinese", "qwen-mt-flash", 12, false);

    private static TranslationResult Denied(TranslationError error) => Ok(string.Empty) with { Error = error };

    private static Func<TranslationRequest, CancellationToken, Task<TranslationResult?>> Gateway(
        Func<TranslationResult> respond, Action? onCall = null) =>
        (_, _) =>
        {
            onCall?.Invoke();
            return Task.FromResult<TranslationResult?>(respond());
        };

    private sealed class StubProvider(Func<TranslationResult> respond) : ITranslationProvider
    {
        public int Calls { get; private set; }

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
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
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(Gateway(() => Ok("网关译文")), own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("网关译文", result.TranslatedText);
        Assert.Null(result.Notice);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task NotSignedIn_UsesTheOwnKeyWithoutNotice()
    {
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            (_, _) => Task.FromResult<TranslationResult?>(null),
            own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("自己的译文", result.TranslatedText);
        Assert.Null(result.Notice);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task QuotaExhausted_FallsBackToTheOwnKeyWithNotice()
    {
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("自己的译文", result.TranslatedText);
        Assert.Equal("本月额度已用完，这次用了你自己的 Key", result.Notice);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task GatewayServerError_FallsBackSilently()
    {
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.ServerError, "服务端错误"))),
            own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("自己的译文", result.TranslatedText);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Cancelled_DoesNotFallBack()
    {
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(Gateway(() => Denied(TranslationError.Cancelled())), own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task OwnKeyFailingToo_ReportsItsOwnError()
    {
        var own = new StubProvider(() => Denied(TranslationError.Authentication()));
        using var provider = new GatewayTranslationProvider(
            Gateway(() => Denied(new TranslationError(TranslationErrorCode.QuotaExhausted, "本月额度已用完"))),
            own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error!.Code);
        Assert.Null(result.Notice);
    }
}
