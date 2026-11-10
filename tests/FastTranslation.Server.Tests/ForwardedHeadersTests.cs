using System.Net;
using System.Net.Http.Json;
using System.Text;
using FastTranslation.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// v118: 反向代理后按 IP 限流的回归测试。生产只有 Caddy 能连 127.0.0.1:5080，
/// 测试用启动过滤器把对端设成 loopback / 非 loopback，再发 X-Forwarded-For。
/// 限流键读的是 RemoteIpAddress，所以 1 次/分就能把「用的是哪个 IP」断言出来。
/// </summary>
public class ForwardedHeadersTests
{
    [Fact]
    public async Task Forwarded_for_is_the_rate_limit_key_behind_a_trusted_proxy()
    {
        using var factory = new RemoteIpFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "203.0.113.8")).StatusCode);
    }

    [Fact]
    public async Task Only_the_last_forwarded_hop_counts()
    {
        using var factory = new RemoteIpFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.1, 203.0.113.9")).StatusCode);
        // 第一个条目不同、最后一跳相同：仍落在同一个限流分区。
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "198.51.100.2, 203.0.113.9")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_is_ignored_when_the_peer_is_not_loopback()
    {
        using var factory = new RemoteIpFactory(IPAddress.Parse("203.0.113.50"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.1")).StatusCode);
        // 对端不是受信代理，两个不同的伪造 XFF 都应按对端 IP 落在同一分区。
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "198.51.100.2")).StatusCode);
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Content = JsonContent.Create(new LoginRequest("unknown@example.com", "password123", "fp-forwarded", "pc"));
        return await client.SendAsync(request);
    }

    private sealed class RemoteIpFactory(IPAddress remoteIp) : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            "ft-forwarded-tests",
            Guid.NewGuid().ToString("N") + ".db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:DatabasePath"] = _databasePath,
                ["Server:SigningKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-signing-key-32b!")),
                ["Server:RequireEmailVerification"] = "false",
                ["Server:AdminToken"] = "test-admin-token",
                ["Server:AuthRequestsPerMinute"] = "1",
            }));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
            {
                return;
            }

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

    private sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIp;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
