using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>每日消费上限,即使每个账号还有额度,也能阻止账单失控。</summary>
public sealed class TinyBudgetGatewayFactory : GatewayFactory
{
    protected override long DailyBudgetMicros => 1;
}

public class GatewayBudgetTests : IClassFixture<TinyBudgetGatewayFactory>
{
    private readonly TinyBudgetGatewayFactory _factory;

    public GatewayBudgetTests(TinyBudgetGatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Chat_StopsAtTheDeploymentDailyBudget()
    {
        var client = _factory.CreateClient();
        var email = $"budget-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        using (var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
        {
            Content = JsonContent.Create(new PlanChangeRequest("light")),
        })
        {
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        var first = await SendAsync(client, 200);
        first.EnsureSuccessStatusCode();

        var refused = await SendAsync(client, 200);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("budget_exhausted", await refused.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, int chars) =>
        client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = new string('a', chars) } },
        });
}

/// <summary>未配置开发者 Key 时,网关必须直接拒绝,而不是把请求转发出去却无处可去。</summary>
public class GatewayDisabledTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public GatewayDisabledTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Chat_ReturnsServiceUnavailableWhenGatewayIsOff()
    {
        var client = _factory.CreateClient();
        var email = $"off-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("gateway_disabled", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>v129: 日预算的并发闸门。进程内预留，结算后释放。</summary>
public sealed class GatewayBudgetGuardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ft-budget-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly ServerDatabase _database;
    private readonly AccountStore _store;

    public GatewayBudgetGuardTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _database = new ServerDatabase(_path);
        _database.Initialize();
        _store = new AccountStore(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ConcurrentReservationsStopAtTheDailyBudget()
    {
        var guard = new GatewayBudgetGuard(_store);

        Assert.True(guard.TryReserve("gateway", 100, 60));
        // 第一笔尚未结算时，第二笔不能也按「预算还有 100」放行。
        Assert.False(guard.TryReserve("gateway", 100, 60));

        var user = _store.CreateUser("budget-guard@example.com", "hash", PlanTier.Free);
        var device = _store.RegisterDevice(user.Id, "fp-budget", "pc").Device!;
        _store.RecordUsage(user.Id, device.Id, 0, "qwen-mt-flash", 0, 0, 60, _database.NowUnix);
        guard.Release("gateway", 60);

        // 已记入 60 微元，下一笔 60 会越过 100，必须拒绝。
        Assert.False(guard.TryReserve("gateway", 100, 60));
    }

    [Fact]
    public void DifferentSourcesHaveIndependentBudgets()
    {
        var guard = new GatewayBudgetGuard(_store);

        Assert.True(guard.TryReserve("gateway", 100, 60));
        Assert.True(guard.TryReserve("speech", 100, 60));

        Assert.False(guard.TryReserve("gateway", 100, 60));
    }

    [Fact]
    public void DisabledBudgetAlwaysAllows()
    {
        var guard = new GatewayBudgetGuard(_store);
        Assert.True(guard.TryReserve("gateway", 0, 1_000_000));
    }
}
