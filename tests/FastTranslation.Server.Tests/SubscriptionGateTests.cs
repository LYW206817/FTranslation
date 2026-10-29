using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>保存就是付费墙(D8):免费或已过期账号必须被拒绝,而不是降级。</summary>
public class SubscriptionGateTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public SubscriptionGateTests(ServerFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, string Email)> SignedInAsync(PlanTier plan, int? days = null)
    {
        var client = _factory.CreateClient();
        var email = $"gate-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free || days is not null)
        {
            await SetPlanAsync(client, email, Plans.ToWire(plan), days);
        }

        return (client, email);
    }

    private static async Task SetPlanAsync(HttpClient client, string email, string plan, int? days)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
        {
            Content = JsonContent.Create(new PlanChangeRequest(plan, days)),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string source = "meticulous") =>
        client.PostAsJsonAsync("/api/v1/notes/", new SaveNoteRequest(null, source, "一丝不苟的", "en", "zh", "qwen-mt-flash", "chrome"));

    [Fact]
    public async Task Save_RefusesFreeAccounts()
    {
        var (client, _) = await SignedInAsync(PlanTier.Free);

        var response = await SaveAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("subscription_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Save_AllowsPaidAccounts()
    {
        var (client, _) = await SignedInAsync(PlanTier.Light);

        var response = await SaveAsync(client);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Save_RefusesExpiredSubscriptions()
    {
        var (client, email) = await SignedInAsync(PlanTier.Light, days: 1);

        // 到期时间存为绝对时刻;把它改写到过去是
        // 如实测试时钟的办法,而不是改套餐字段。
        var accounts = _factory.Services.GetRequiredService<AccountStore>();
        var user = accounts.FindUserByEmail(email)!;
        Assert.True(accounts.SetPlan(user.Id, PlanTier.Light, DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()));

        var response = await SaveAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("subscription_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Read_StaysOpenForFreeAccounts()
    {
        var (client, _) = await SignedInAsync(PlanTier.Free);

        var response = await client.GetAsync("/api/v1/notes/");

        // 读取绝不能被闸门拦住:在付费期间保存过的账号,其词库仍可查看。
        response.EnsureSuccessStatusCode();
    }
}
