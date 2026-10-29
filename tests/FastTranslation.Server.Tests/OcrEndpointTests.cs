using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary><c>POST /v1/ocr</c> 之所以存在,是因为聊天端点无法携带图片:它只统计
/// <c>content</c> 字符串,所以纯图片请求体被读成零字符并遭拒绝。这些测试
/// 锁定额外端点的三项价值——它转发到视觉路径、按视觉价格计费,
/// 而且被拒绝时绝不花钱。</summary>
public class OcrEndpointTests : IClassFixture<GatewayFactory>, IClassFixture<ServerFactory>
{
    private readonly GatewayFactory _gateway;
    private readonly ServerFactory _plain;

    public OcrEndpointTests(GatewayFactory gateway, ServerFactory plain)
    {
        _gateway = gateway;
        _plain = plain;
    }

    /// <summary>与桌面客户端发送的形状一致的 OCR 请求体:一张图片,没有文本。</summary>
    private static Task<HttpResponseMessage> OcrAsync(HttpClient client, string? device = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/ocr")
        {
            Content = JsonContent.Create(new
            {
                model = "qwen-vl-ocr",
                input = new
                {
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[] { new { image_url = "data:image/png;base64,AAAA" } },
                        },
                    },
                },
            }),
        };

        if (device is not null)
        {
            request.Headers.Add(GatewayEndpointConstants.DeviceHeader, device);
        }

        return client.SendAsync(request);
    }

    private static async Task<HttpClient> SignedInClientAsync(
        WebApplicationFactory<Program> factory,
        PlanTier plan,
        string? device = null)
    {
        var client = factory.CreateClient();
        var email = $"ocr-{Guid.NewGuid():N}@example.com";
        var fingerprint = device ?? "fp-" + Guid.NewGuid().ToString("N");

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, fingerprint, "test-pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free)
        {
            using var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
            {
                Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), null)),
            };
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        return client;
    }

    private static async Task<string> EmailAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!.Email;
    }

    private static Guid UserId(GatewayFactory factory, string email)
        => factory.Services.GetRequiredService<AccountStore>().FindUserByEmail(email)!.Id;

    private static (string Model, long Chars, long CostMicros) LastUsageEvent(GatewayFactory factory, Guid userId)
    {
        var database = factory.Services.GetRequiredService<ServerDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, chars, cost_micros FROM usage_events
            WHERE user_id = $userId ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static int UsageEventCount(GatewayFactory factory, Guid userId)
    {
        var database = factory.Services.GetRequiredService<ServerDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events WHERE user_id = $userId";
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task Ocr_RequiresAuthentication()
    {
        var anonymous = _gateway.CreateClient();

        var response = await OcrAsync(anonymous);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ocr_DisabledGatewayIs503()
    {
        // 普通工厂没有 API Key,这正是默认部署:它必须拒绝,而不是猜测。
        var client = await SignedInClientAsync(_plain, PlanTier.Light);

        var response = await OcrAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("gateway_disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ocr_EmptyBodyIs400()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);

        var response = await client.PostAsync("/v1/ocr", new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("body_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ocr_ForwardsToTheVisionEndpointWithDeveloperKey()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var before = _gateway.Upstream.Calls.Count;

        var response = await OcrAsync(client);

        response.EnsureSuccessStatusCode();
        var call = _gateway.Upstream.Calls[before];
        Assert.Equal("http://upstream.test/ocr", call.Url);
        // 客户端的 Bearer 令牌绝不能泄漏到上游;开发者 Key 会替换它。
        Assert.Equal(GatewayFactory.UpstreamKey, call.Authorization);
        Assert.Contains("image_url", call.Body);
    }

    [Fact]
    public async Task Ocr_SuccessIsBilledAtVisionPrice()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);

        (await OcrAsync(client)).EnsureSuccessStatusCode();

        var (model, chars, costMicros) = LastUsageEvent(_gateway, UserId(_gateway, email));
        Assert.Equal("qwen-vl-ocr", model);
        // 额度按一张截图计量,所以 OCR 不可能比它来源的那张图更便宜。
        Assert.Equal(1_000, chars);
        Assert.Equal(Pricing.TextCostMicros("qwen-vl-ocr", 1_000, 0), costMicros);
        Assert.True(costMicros > 0);
    }

    [Fact]
    public async Task Ocr_UsesReportedUsageWhenUpstreamSuppliesIt()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);
        _gateway.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""
            {"output":{"choices":[{"message":{"content":[{"text":"hello"}]}}]},"usage":{"input_tokens":1500,"output_tokens":40}}
            """);

        try
        {
            (await OcrAsync(client)).EnsureSuccessStatusCode();
        }
        finally
        {
            _gateway.Upstream.Reset();
        }

        var (_, _, costMicros) = LastUsageEvent(_gateway, UserId(_gateway, email));
        // 视觉 token 才是模型实际计费的东西,所以它们必须压过 1000 字符的兜底。
        Assert.Equal(Pricing.TextCostMicros("qwen-vl-ocr", 1_500, 40), costMicros);
    }

    [Fact]
    public async Task Ocr_ForwardsUpstreamFailureWithoutBilling()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);
        var userId = UserId(_gateway, email);
        var before = UsageEventCount(_gateway, userId);
        _gateway.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""{"error":{"message":"nope"}}""", HttpStatusCode.BadRequest);

        try
        {
            var response = await OcrAsync(client);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, UsageEventCount(_gateway, userId));
        }
        finally
        {
            _gateway.Upstream.Reset();
        }
    }

    [Fact]
    public async Task Ocr_RefusesWhenFreeDailyQuotaIsSpent()
    {
        // Free 每天允许 2000 字符,所以两张截图(各 1000)就会耗尽它。
        var client = await SignedInClientAsync(_gateway, PlanTier.Free);

        (await OcrAsync(client)).EnsureSuccessStatusCode();
        (await OcrAsync(client)).EnsureSuccessStatusCode();

        var refused = await OcrAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("daily_quota_exhausted", refused.Headers.GetValues("X-FT-Quota-Reason").Single());
    }

    [Fact]
    public void Pricing_KnowsTheVisionModel()
    {
        // 缺少条目会静默按 flash 价格计费,这会严重低报视觉成本。
        var price = Pricing.For("qwen-vl-ocr");

        Assert.Equal("qwen-vl-ocr", price.Model);
        Assert.True(price.InputPerMillionTokens > 0m);
    }
}
