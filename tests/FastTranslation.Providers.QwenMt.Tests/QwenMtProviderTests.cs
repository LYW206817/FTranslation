using System.Net;
using System.Text;
using FastTranslation.Core;
using FastTranslation.Providers.QwenMt;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

/// <summary>Fake upstream on a local HttpListener — no network, no real key.</summary>
public sealed class FakeQwenServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public Func<HttpListenerRequest, (int status, string body)> Handler = _ =>
        (200, "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
    public List<string> Bodies = new();
    public List<string> AuthHeaders = new();

    public string Url { get; } = "";
    public HttpListenerException? ListenerException { get; private set; }

    public FakeQwenServer()
    {
        // Bind by retry: a random port can already be taken by another test run or a stray
        // process, and HttpListener.Start throws rather than picking a free one on its own.
        var started = false;
        ListenerException = null;
        for (var attempt = 0; attempt < 20 && !started; attempt++)
        {
            var port = 18500 + Random.Shared.Next(2000);
            var candidate = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(candidate);
            try
            {
                _listener.Start();
                Url = candidate + "v1";
                started = true;
            }
            catch (HttpListenerException ex)
            {
                ListenerException = ex;
            }
        }

        if (!started)
        {
            throw new InvalidOperationException(
                $"could not bind a local HttpListener port after 20 attempts: {ListenerException?.Message}",
                ListenerException);
        }

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                lock (Bodies) { Bodies.Add(body); }
                AuthHeaders.Add(ctx.Request.Headers["Authorization"] ?? "");
                var (status, resp) = Handler(ctx.Request);
                var bytes = Encoding.UTF8.GetBytes(resp);
                ctx.Response.StatusCode = status;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}

public class QwenMtProviderTests
{
    private static TranslationRequest Request() => new("hello", "auto", "Chinese", "qwen-mt-flash");

    [Fact]
    public async Task Success_ParsesContent()
    {
        using var server = new FakeQwenServer();
        using var provider = new QwenMtProvider(() => "sk-test", new QwenMtOptions { BaseUrl = server.Url });
        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("你好", result.TranslatedText);

        var body = server.Bodies.Single();
        Assert.Contains("\"translation_options\"", body);
        Assert.Contains("\"target_lang\":\"Chinese\"", body);
        Assert.Contains("\"role\":\"user\"", body);
        Assert.Equal("Bearer sk-test", server.AuthHeaders.Single());
    }

    [Fact]
    public async Task Success_ReadsUsageFromTheSameBody()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (200, """
                {"choices":[{"message":{"content":"你好"}}],"usage":{"prompt_tokens":56,"completion_tokens":9,"total_tokens":65}}
                """),
        };
        using var provider = new QwenMtProvider(() => "sk-test", new QwenMtOptions { BaseUrl = server.Url });

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(56, result.Usage?.InputTokens);
        Assert.Equal(9, result.Usage?.OutputTokens);
        Assert.Equal(65, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Success_WithoutUsageLeavesItNull()
    {
        using var server = new FakeQwenServer();
        using var provider = new QwenMtProvider(() => "sk-test", new QwenMtOptions { BaseUrl = server.Url });

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        // Null means "unknown"; reporting 0 would silently claim the request was free.
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Failure_CarriesNoUsage()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (500, "{\"error\":{\"message\":\"boom\"}}"),
        };
        using var provider = new QwenMtProvider(() => "sk-bad", new QwenMtOptions { BaseUrl = server.Url });

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.ServerError, result.Error?.Code);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Unauthorized_MapsToAuthentication()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (401, "{\"error\":{\"message\":\"bad key\"}}"),
        };
        using var provider = new QwenMtProvider(() => "sk-bad", new QwenMtOptions { BaseUrl = server.Url });
        var result = await provider.TranslateAsync(Request(), CancellationToken.None);
        Assert.Equal(TranslationErrorCode.Authentication, result.Error?.Code);
    }

    [Fact]
    public async Task RateLimited_Retries_ThenSucceeds()
    {
        var calls = 0;
        using var server = new FakeQwenServer
        {
            Handler = _ => Interlocked.Increment(ref calls) == 1
                ? (429, "{}")
                : (200, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"),
        };
        using var provider = new QwenMtProvider(() => "sk", new QwenMtOptions { BaseUrl = server.Url });
        var result = await provider.TranslateAsync(Request(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MalformedSuccessBody_MapsToInvalidResponse()
    {
        using var server = new FakeQwenServer { Handler = _ => (200, "{\"choices\":[]}") };
        using var provider = new QwenMtProvider(() => "sk", new QwenMtOptions { BaseUrl = server.Url });
        var result = await provider.TranslateAsync(Request(), CancellationToken.None);
        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error?.Code);
    }

    [Fact]
    public async Task MissingKey_FailsImmediately()
    {
        using var provider = new QwenMtProvider(() => null);
        var result = await provider.TranslateAsync(Request(), CancellationToken.None);
        Assert.Equal(TranslationErrorCode.Authentication, result.Error?.Code);
    }

    [Fact]
    public async Task TooLong_NeverCallsNetwork()
    {
        using var server = new FakeQwenServer();
        using var provider = new QwenMtProvider(() => "sk", new QwenMtOptions { BaseUrl = server.Url });
        var result = await provider.TranslateAsync(
            new TranslationRequest(new string('x', 5001), "auto", "Chinese", "qwen-mt-flash"),
            CancellationToken.None);
        Assert.Equal(TranslationErrorCode.ContentTooLong, result.Error?.Code);
        Assert.Empty(server.Bodies);
    }
[Fact]
    public async Task GatewayQuotaRefusal_MapsToQuotaExhaustedWithoutRetry()
    {
        var calls = 0;
        using var server = new FakeQwenServer
        {
            Handler = _ =>
            {
                Interlocked.Increment(ref calls);
                return (429, "{\"error\":\"monthly_quota_exhausted\"}");
            },
        };
        using var provider = new QwenMtProvider(() => "token", new QwenMtOptions { BaseUrl = server.Url });

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        // 额度用尽是"换个 Key 就行"，不是"稍后再试"，所以不能走 429 的重试分支
        Assert.Equal(TranslationErrorCode.QuotaExhausted, result.Error?.Code);
        Assert.Equal("本月额度已用完", result.Error?.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExtraHeaders_AreAddedToTheRequest()
    {
        string? seen = null;
        using var server = new FakeQwenServer
        {
            Handler = ctx =>
            {
                seen = ctx.Headers["X-FT-Device"];
                return (200, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            },
        };
        using var provider = new QwenMtProvider(
            () => "token",
            new QwenMtOptions
            {
                BaseUrl = server.Url,
                ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = "fp-1" },
            });

        var result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("fp-1", seen);
    }
}

