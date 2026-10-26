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

    public string Url { get; }

    public FakeQwenServer()
    {
        var port = 18500 + new Random().Next(2000);
        Url = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
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
}
