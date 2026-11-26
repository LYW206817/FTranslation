using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using FastTranslation.Core.Account;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public sealed class WindowsAccountProxyTests
{
    [Fact]
    public async Task Next_usage_request_uses_the_new_proxy_then_goes_direct_when_disabled()
    {
        const string proxyA = "http://127.0.0.1:41231";
        const string proxyB = "http://127.0.0.1:41232";
        string? systemProxy = proxyA;
        var routes = new List<string?>();
        HttpMessageHandler CreateTransport(string? proxy)
        {
            routes.Add(proxy);
            return new UsageTransport(proxy);
        }

        using var http = new HttpClient(new WindowsAccountHttpHandler(
            () => new WindowsProxySettings(false, null, systemProxy, null),
            settings => CreateTransport(settings.ProxyServer)));
        var account = new AccountApiClient(http, "https://example.com");

        Assert.Equal(1, (await account.GetUsageAsync("synthetic", CancellationToken.None)).Value?.UsedMonthlyChars);
        systemProxy = proxyB;
        Assert.Equal(2, (await account.GetUsageAsync("synthetic", CancellationToken.None)).Value?.UsedMonthlyChars);
        systemProxy = null;
        Assert.Equal(3, (await account.GetUsageAsync("synthetic", CancellationToken.None)).Value?.UsedMonthlyChars);
        Assert.Equal(3, (await account.GetUsageAsync("synthetic", CancellationToken.None)).Value?.UsedMonthlyChars);
        Assert.Equal(new[] { proxyA, proxyB, null }, routes);
    }

    [Fact]
    public async Task Proxy_switch_does_not_close_a_response_that_is_still_being_read()
    {
        var oldBody = new PendingUsageContent();
        var oldRoute = new UsageTransport("http://127.0.0.1:41231", oldBody);
        var newRoute = new UsageTransport(null);
        var settings = new WindowsProxySettings(false, null, "http://127.0.0.1:41231", null);
        using var http = new HttpClient(new WindowsAccountHttpHandler(
            () => settings, proxy => proxy.ProxyServer is null ? newRoute : oldRoute));
        var account = new AccountApiClient(http, "https://example.com");

        var original = account.GetUsageAsync("synthetic", CancellationToken.None);
        await oldBody.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            settings = default;
            Assert.Equal(3, (await account.GetUsageAsync("synthetic", CancellationToken.None)).Value?.UsedMonthlyChars);
            Assert.False(oldRoute.IsDisposed);
        }
        finally
        {
            oldBody.Release.TrySetResult();
        }

        Assert.Equal(1, (await original).Value?.UsedMonthlyChars);
        Assert.True(oldRoute.IsDisposed);
        Assert.False(newRoute.IsDisposed);
    }

    private sealed class UsageTransport(string? proxy, HttpContent? content = null) : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var used = proxy switch { null => 3, "http://127.0.0.1:41231" => 1, _ => 2 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content ?? JsonContent.Create(new { usedMonthlyChars = used, quotaMonthlyChars = 100 }),
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PendingUsageContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync("""{"usedMonthlyChars":1,"quotaMonthlyChars":100}"""u8.ToArray(), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
