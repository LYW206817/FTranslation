using System.Net;
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

    [Theory]
    [InlineData("pending", HttpStatusCode.GatewayTimeout, false)]
    [InlineData("already_processed", HttpStatusCode.Conflict, false)]
    [InlineData(null, HttpStatusCode.BadGateway, false)]
    [InlineData(null, HttpStatusCode.TooManyRequests, true)]
    public async Task Gateway_operation_receipts_stop_automatic_fallback(
        string? outcome, HttpStatusCode status, bool loseFirstResponse)
    {
        var operation = Guid.NewGuid().ToString("D");
        var handler = new OutcomeHandler(outcome, status, loseFirstResponse);
        using var client = new HttpClient(handler);
        using var gateway = new QwenMtProvider(() => "test-token", new QwenMtOptions
        {
            BaseUrl = "https://example.com/v1",
            ExtraHeaders = new Dictionary<string, string> { ["X-FT-Request-Id"] = operation },
        }, client);
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            async (request, token) => await gateway.TranslateAsync(request, token), own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.GatewayResultUnavailable, result.Error?.Code);
        Assert.Equal(0, own.Calls);
        Assert.Equal(loseFirstResponse ? 2 : 1, handler.Operations.Count);
        Assert.All(handler.Operations, identity => Assert.Equal(operation, identity));
    }

    private sealed class OutcomeHandler(string? outcome, HttpStatusCode status, bool loseFirstResponse) : HttpMessageHandler
    {
        public List<string> Operations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Operations.Add(Assert.Single(request.Headers.GetValues("X-FT-Request-Id")));
            if (loseFirstResponse && Operations.Count == 1) throw new IOException("Fixture connection interrupted.");
            var response = new HttpResponseMessage(status);
            if (outcome is not null) response.Headers.Add("X-FT-Request-Outcome", outcome);
            return Task.FromResult(response);
        }
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

    [Fact]
    public async Task GatewayThrowing_FallsBackToTheOwnKey()
    {
        // 取令牌、拼地址这些步骤在网关一侧，抛出来的异常与翻译本身无关；整次请求不该只剩
        //「翻译失败,请重试」这一句
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            (_, _) => throw new HttpRequestException("网关炸了"),
            own);

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal("自己的译文", result.TranslatedText);
        Assert.Equal(1, own.Calls);
    }

    [Fact]
    public async Task CallerCancelled_StillThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var own = new StubProvider(() => Ok("自己的译文"));
        using var provider = new GatewayTranslationProvider(
            (_, _) => throw new OperationCanceledException(cts.Token),
            own);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateAsync(Request(), cts.Token));
        Assert.Equal(0, own.Calls);
    }
}
