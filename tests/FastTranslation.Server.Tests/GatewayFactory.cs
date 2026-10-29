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

/// <summary>One recorded call the gateway made upstream, so tests can assert what it forwarded.</summary>
public sealed record UpstreamCall(string Url, string Body, string? Authorization);

/// <summary>
/// Stands in for DashScope: no network, no key, and every answer is scriptable. The gateway is the
/// only thing under test, so the seam is deliberately the HTTP handler rather than a fake provider.
/// </summary>
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

/// <summary>
/// Boots the app with the gateway enabled and DashScope replaced by <see cref="StubUpstreamHandler"/>.
/// Everything else (database, auth, quota tables) is the production wiring.
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "ft-gateway-tests",
        Guid.NewGuid().ToString("N") + ".db");

    public const string UpstreamKey = "test-upstream-key";

    public StubUpstreamHandler Upstream { get; } = new();

    /// <summary>Defaults to effectively unlimited; the budget test lowers it.</summary>
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
                    // Best effort cleanup.
                }
            }
        }
    }
}
