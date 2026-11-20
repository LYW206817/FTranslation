using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>免费桶只有 1 微元、会员桶宽松:用来证明免费用户烧光自己的预算,不会连坐会员。</summary>
public sealed class TinyBudgetGatewayFactory : GatewayFactory
{
    protected override long DailyBudgetMicros => 1;
}

/// <summary>会员桶只有 1 微元、免费桶宽松:用来证明会员桶有自己的上限,且不拦免费用户。</summary>
public sealed class TinyPaidBudgetGatewayFactory : GatewayFactory
{
    protected override long PaidDailyBudgetMicros => 1;
}

/// <summary>注册账号并发翻译请求的公共步骤;用户数据全部现编。</summary>
internal static class BudgetTestClient
{
    /// <param name="expiresAtUnix">订阅到期时间;null 表示没有到期时间的永久授权。</param>
    public static async Task<HttpClient> RegisterAsync(GatewayFactory factory, PlanTier plan, long? expiresAtUnix = null)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"budget-{Guid.NewGuid():N}@example.com", "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free)
        {
            Assert.True(factory.Services.GetRequiredService<AccountStore>().SetPlan(auth.Account.Id, plan, expiresAtUnix));
        }

        return client;
    }

    public static Task<HttpResponseMessage> TranslateAsync(HttpClient client) =>
        client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = new string('a', 200) } },
        });

    public static async Task AssertBudgetRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("budget_exhausted", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>每日消费上限按「有效档位」分桶:免费桶与会员桶互不连坐,各自阻止账单失控。</summary>
public class GatewayBudgetTests : IClassFixture<TinyBudgetGatewayFactory>
{
    private readonly TinyBudgetGatewayFactory _factory;

    public GatewayBudgetTests(TinyBudgetGatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Chat_StopsFreeAccountsAtTheFreeDailyBudget()
    {
        var free = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Free);

        // 免费桶只有 1 微元:同一天最多放行「第一笔」,同类测试先跑过的话第一笔也会被拒。
        await BudgetTestClient.TranslateAsync(free);
        await BudgetTestClient.AssertBudgetRefusedAsync(await BudgetTestClient.TranslateAsync(free));
    }

    [Fact]
    public async Task Paid_member_keeps_translating_after_the_free_bucket_is_exhausted()
    {
        await ExhaustFreeBucketAsync();
        var store = _factory.Services.GetRequiredService<AccountStore>();
        var freeBefore = store.ReadTodayCostMicros("gateway");
        var member = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Light);

        var response = await BudgetTestClient.TranslateAsync(member);

        response.EnsureSuccessStatusCode();
        // 会员的花费记进自己的桶,既不占用也不推高免费桶。
        Assert.True(store.ReadTodayCostMicros("gateway_paid") > 0);
        Assert.Equal(freeBefore, store.ReadTodayCostMicros("gateway"));
    }

    [Fact]
    public async Task Expired_subscription_is_budgeted_as_a_free_account()
    {
        await ExhaustFreeBucketAsync();
        var database = _factory.Services.GetRequiredService<ServerDatabase>();
        var lapsed = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Light, database.NowUnix - 60);

        await BudgetTestClient.AssertBudgetRefusedAsync(await BudgetTestClient.TranslateAsync(lapsed));
    }

    private async Task ExhaustFreeBucketAsync()
    {
        var free = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Free);
        await BudgetTestClient.TranslateAsync(free);
        await BudgetTestClient.AssertBudgetRefusedAsync(await BudgetTestClient.TranslateAsync(free));
    }
}

public class GatewayPaidBudgetTests : IClassFixture<TinyPaidBudgetGatewayFactory>
{
    private readonly TinyPaidBudgetGatewayFactory _factory;

    public GatewayPaidBudgetTests(TinyPaidBudgetGatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Paid_members_stop_at_the_paid_budget_without_stopping_free_accounts()
    {
        var member = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Light);
        await BudgetTestClient.TranslateAsync(member);
        await BudgetTestClient.AssertBudgetRefusedAsync(await BudgetTestClient.TranslateAsync(member));

        var free = await BudgetTestClient.RegisterAsync(_factory, PlanTier.Free);
        (await BudgetTestClient.TranslateAsync(free)).EnsureSuccessStatusCode();
    }
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
        _store.RecordUsage(user.Id, device.Id, 0, "qwen-mt-flash", 0, 0, 60, _database.NowUnix, "gateway");
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
