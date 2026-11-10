using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FastTranslation.Server.Gateway;

namespace FastTranslation.Server.Tests;

/// <summary>网关向上游发出的一次已记录调用,便于测试断言它转发了什么。</summary>
public sealed record UpstreamCall(string Url, string Body, string? Authorization);

/// <summary>代替 DashScope:不联网、不需要 Key,每个回答都可编排。被测的只有
/// 网关本身,所以接缝刻意选在 HTTP handler,而不是伪造 provider。</summary>
public sealed class StubUpstreamHandler : HttpMessageHandler
{
    private readonly List<UpstreamCall> _calls = [];

    public IReadOnlyList<UpstreamCall> Calls => _calls;

    public Func<HttpRequestMessage, string, HttpResponseMessage>? Responder { get; set; }

    public void Reset()
    {
        _calls.Clear();
        Responder = null;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        _calls.Add(new UpstreamCall(request.RequestUri?.ToString() ?? string.Empty, body, request.Headers.Authorization?.Parameter));
        return Responder is { } responder
            ? responder(request, body)
            : Json("""{"choices":[{"message":{"content":"translated"}}]}""");
    }
}

/// <summary>启动启用了网关、DashScope 被 <see cref="StubUpstreamHandler"/> 替换的应用。
/// 其余部分(数据库、认证、额度表)都是生产接线。</summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "ft-gateway-tests",
        Guid.NewGuid().ToString("N") + ".db");

    public const string UpstreamKey = "test-upstream-key";

    public StubUpstreamHandler Upstream { get; } = new();

    /// <summary>默认实际上不设上限;预算测试会把它调低。</summary>
    protected virtual long DailyBudgetMicros => 5_000_000;

    protected virtual int RequestsPerMinute => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:DatabasePath"] = _databasePath,
            ["Server:SigningKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-signing-key-32b!")),
            ["Server:RequireEmailVerification"] = "false",
            ["Server:AdminToken"] = ServerFactory.AdminToken,
            ["Server:AuthRequestsPerMinute"] = "1000",
            ["Server:AdminRequestsPerMinute"] = "1000",
            ["Server:AccessTokenMinutes"] = "30",
            ["Server:Gateway:Enabled"] = "true",
            ["Server:Gateway:ApiKey"] = UpstreamKey,
            ["Server:Gateway:BaseUrl"] = "http://upstream.test/v1",
            ["Server:Gateway:ImageBaseUrl"] = "http://upstream.test/image",
            ["Server:Gateway:OcrBaseUrl"] = "http://upstream.test/ocr",
            ["Server:Gateway:DailyBudgetMicros"] = DailyBudgetMicros.ToString(),
            ["Server:Gateway:RequestsPerMinute"] = RequestsPerMinute.ToString(),
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DashScopeGateway>();
            services.AddSingleton(sp => new DashScopeGateway(sp.GetRequiredService<GatewayOptions>(), Upstream));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(_databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }
}
