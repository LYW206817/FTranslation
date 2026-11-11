using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

public class GatewayEndpointTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public GatewayEndpointTests(GatewayFactory factory)
    {
        _factory = factory;
    }

    /// <summary>按桌面客户端的方式发送聊天请求:Bearer 令牌 + 设备头。</summary>
    private static Task<HttpResponseMessage> ChatAsync(HttpClient client, int chars, string? device = null, string model = "qwen-mt-flash")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = new[] { new { role = "user", content = new string('a', chars) } },
                translation_options = new { source_lang = "en", target_lang = "zh" },
            }),
        };

        if (device is not null)
        {
            request.Headers.Add(GatewayEndpointConstants.DeviceHeader, device);
        }

        return client.SendAsync(request);
    }

    private async Task<HttpClient> SignedInClientAsync(PlanTier plan, string? device = null, int? planDays = null)
    {
        var client = _factory.CreateClient();
        var email = $"gw-{Guid.NewGuid():N}@example.com";
        var fingerprint = device ?? "fp-" + Guid.NewGuid().ToString("N");

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, fingerprint, "test-pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free || planDays is not null)
        {
            using var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
            {
                Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), planDays)),
            };
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        return client;
    }

    private static async Task<UsageDto> ReadUsageAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/usage");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UsageDto>())!;
    }

    private async Task<int> CallCountAsync()
    {
        await Task.Yield();
        return _factory.Upstream.Calls.Count;
    }

    [Fact]
    public async Task Gateway_RequiresAuthentication()
    {
        var anonymous = _factory.CreateClient();
        var response = await ChatAsync(anonymous, 10);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_ForwardsToDashScopeWithDeveloperKey()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var before = await CallCountAsync();

        var response = await ChatAsync(client, 20);

        response.EnsureSuccessStatusCode();
        var call = _factory.Upstream.Calls[before];
        Assert.Equal("http://upstream.test/v1/chat/completions", call.Url);
        // 客户端自己的 Bearer 令牌绝不能转发出去;开发者 Key 会替换它。
        Assert.Equal(GatewayFactory.UpstreamKey, call.Authorization);
        Assert.Contains("translation_options", call.Body);
    }

    [Fact]
    public async Task Chat_ChargesTheAccountAndReturnsUpstreamBody()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var before = await ReadUsageAsync(client);

        var response = await ChatAsync(client, 120);

        response.EnsureSuccessStatusCode();
        Assert.Contains("translated", await response.Content.ReadAsStringAsync());

        var after = await ReadUsageAsync(client);
        Assert.Equal(before.UsedMonthlyChars + 120, after.UsedMonthlyChars);
        Assert.Equal(before.RemainingMonthlyChars - 120, after.RemainingMonthlyChars);
    }

    [Fact]
    public async Task PlusChat_BillsFlashEquivalentChars()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var before = await ReadUsageAsync(client);

        (await ChatAsync(client, 100, model: "qwen-mt-plus")).EnsureSuccessStatusCode();

        var after = await ReadUsageAsync(client);
        Assert.Equal(before.UsedMonthlyChars + 270, after.UsedMonthlyChars);
    }

    [Fact]
    public async Task PlusPrecheck_ReservesTheMultipliedQuota_WhileFlashStillFits()
    {
        // 只改记账不改预检的话,plus 能把剩余额度透支掉:闸门按 100 放行,账上却记 270。
        // 这里把日额度压到剩 200,同一个额度下 plus(需 270)必须被拒、flash(需 100)必须能过。
        var client = await SignedInClientAsync(PlanTier.Free);
        (await ChatAsync(client, 1800)).EnsureSuccessStatusCode();

        var plus = await ChatAsync(client, 100, model: "qwen-mt-plus");
        Assert.Equal(HttpStatusCode.TooManyRequests, plus.StatusCode);

        (await ChatAsync(client, 100)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Chat_UsesReportedUsageWhenUpstreamSuppliesIt()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var email = await EmailAsync(client);
        _factory.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""
            {"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":900,"completion_tokens":100}}
            """);

        try
        {
            (await ChatAsync(client, 10)).EnsureSuccessStatusCode();
        }
        finally
        {
            _factory.Upstream.Reset();
        }

        // 上报的 token 数必须压过基于字符的估算,否则成本统计就是虚的。
        var accounts = _factory.Services.GetRequiredService<AccountStore>();
        var user = accounts.FindUserByEmail(email)!;
        var (tokensIn, tokensOut, costMicros) = LastUsageEvent(user.Id);

        Assert.Equal(900, tokensIn);
        Assert.Equal(100, tokensOut);
        Assert.Equal(Pricing.TextCostMicros("qwen-mt-flash", 900, 100), costMicros);
        Assert.True(costMicros > 0);
    }

    private (long TokensIn, long TokensOut, long CostMicros) LastUsageEvent(Guid userId)
    {
        var database = _factory.Services.GetRequiredService<ServerDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tokens_in, tokens_out, cost_micros FROM usage_events
            WHERE user_id = $userId ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    [Fact]
    public async Task Chat_RefusesWhenMonthlyQuotaIsSpent()
    {
        // Light 没有每日上限,所以这能单独隔离出月度闸门。循环次数跟着额度常量走，
        // 改套餐额度时不要在这里再写一个会悄悄失真的固定次数。
        var client = await SignedInClientAsync(PlanTier.Light);

        for (var i = 0; i < Plans.LightMonthlyChars / 5_000; i++)
        {
            (await ChatAsync(client, 5_000)).EnsureSuccessStatusCode();
        }

        var refused = await ChatAsync(client, 1);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("monthly_quota_exhausted", refused.Headers.GetValues("X-FT-Quota-Reason").Single());
        Assert.Contains("monthly_quota_exhausted", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_RefusesWhenFreeDailyQuotaIsSpent()
    {
        var client = await SignedInClientAsync(PlanTier.Free);

        (await ChatAsync(client, 2_000)).EnsureSuccessStatusCode();

        var refused = await ChatAsync(client, 1);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("daily_quota_exhausted", refused.Headers.GetValues("X-FT-Quota-Reason").Single());
    }

    [Fact]
    public async Task Chat_RejectsOverlongAndEmptyText()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var before = await CallCountAsync();

        var tooLong = await ChatAsync(client, 5_001);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLong.StatusCode);
        Assert.Contains("text_too_long", await tooLong.Content.ReadAsStringAsync());

        var empty = await ChatAsync(client, 0);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("text_required", await empty.Content.ReadAsStringAsync());

        // 两个请求都不能到达 DashScope——被拒绝的请求绝不能花钱。
        Assert.Equal(before, await CallCountAsync());
    }

    [Fact]
    public async Task Chat_ForwardsUpstreamFailureWithoutCharging()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var before = await ReadUsageAsync(client);
        _factory.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""{"error":{"message":"nope"}}""", HttpStatusCode.Unauthorized);

        try
        {
            var response = await ChatAsync(client, 50);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var after = await ReadUsageAsync(client);
            Assert.Equal(before.UsedMonthlyChars, after.UsedMonthlyChars);
        }
        finally
        {
            _factory.Upstream.Reset();
        }
    }

    [Fact]
    public async Task Chat_ChargesAgainstTheSendingDevice()
    {
        var device = "fp-" + Guid.NewGuid().ToString("N");
        var client = await SignedInClientAsync(PlanTier.Free, device);
        var email = await EmailAsync(client);

        (await ChatAsync(client, 1_500, device)).EnsureSuccessStatusCode();

        // 每日账本按设备记账,所以要通过服务端自己使用的 store 去读。
        var accounts = _factory.Services.GetRequiredService<AccountStore>();
        var user = accounts.FindUserByEmail(email)!;
        var registered = accounts.FindDeviceByFingerprint(user.Id, device)!;

        var response = await client.GetAsync($"/api/v1/account/usage?deviceId={registered.Id:D}");
        response.EnsureSuccessStatusCode();
        var usage = (await response.Content.ReadFromJsonAsync<UsageDto>())!;
        Assert.Equal(1_500, usage.UsedDailyChars);
    }

    [Fact]
    public async Task Usage_ResolvesTheDeviceFromTheFingerprint()
    {
        var device = "fp-" + Guid.NewGuid().ToString("N");
        var client = await SignedInClientAsync(PlanTier.Free, device);

        (await ChatAsync(client, 1_200, device)).EnsureSuccessStatusCode();

        // 客户端只知道自己的 fingerprint,永远不知道服务端的 device id。
        var response = await client.GetAsync($"/api/v1/account/usage?fingerprint={device}");
        response.EnsureSuccessStatusCode();
        var usage = (await response.Content.ReadFromJsonAsync<UsageDto>())!;
        Assert.Equal(1_200, usage.UsedDailyChars);
    }

    // v127: 第 4 台设备（或本机设备记录被清掉）会撞上每账号 3 台上限。网关不能把这次
    // 计费写到 Guid.Empty 上——usage_daily 对 devices 有外键，插入会 500，而上游已经
    // 调用并产生成本。应当退回到该账号已有的一台设备记账。
    [Fact]
    public async Task Chat_WithDeviceOverTheLimit_ChargesAnExistingDevice()
    {
        var client = await SignedInClientAsync(PlanTier.Light);
        var email = await EmailAsync(client);
        for (var i = 0; i < 2; i++)
        {
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(email, "password123", $"fp-extra-{i}-{Guid.NewGuid():N}", "extra-pc"));
            login.EnsureSuccessStatusCode();
        }

        var response = await ChatAsync(client, 20, "fp-over-limit-" + Guid.NewGuid().ToString("N"));

        response.EnsureSuccessStatusCode();
        Assert.Equal(20, (await ReadUsageAsync(client)).UsedMonthlyChars);
    }

    private static async Task<string> EmailAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!.Email;
    }
}

/// <summary>与 <c>GatewayEndpoints.DeviceHeaderName</c> 一致,供测试侧使用。</summary>
internal static class GatewayEndpointConstants
{
    public const string DeviceHeader = "X-FT-Device";
}
