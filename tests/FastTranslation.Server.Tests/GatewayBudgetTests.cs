using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
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
