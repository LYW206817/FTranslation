using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

public class AdminEndpointTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;
    private readonly HttpClient _client;

    public AdminEndpointTests(ServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string UniqueEmail() => $"admin-{Guid.NewGuid():N}@example.com";

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> AdminAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Admin_usage_aggregates_by_source_and_day()
    {
        var auth = await RegisterAsync(_client, UniqueEmail());
        var store = _factory.Services.GetRequiredService<AccountStore>();
        var database = _factory.Services.GetRequiredService<ServerDatabase>();
        var device = store.ListDevices(auth.Account.Id).Single();
        var now = database.NowUnix;

        store.RecordUsage(auth.Account.Id, device.Id, 120, "qwen-mt-flash", 10, 5, 1_000, now);
        store.RecordUsage(auth.Account.Id, device.Id, 240, "qwen-audio-3.1-asr-flash-streaming", 20, 10, 2_000, now, "speech");

        var response = await AdminAsync(_client, HttpMethod.Get, "/api/v1/admin/usage?days=1");
        response.EnsureSuccessStatusCode();
        var usage = (await response.Content.ReadFromJsonAsync<AdminUsageResponse>())!;

        Assert.Equal(1, usage.Days);
        Assert.True(usage.Total.Requests >= 2);
        Assert.True(usage.Total.Chars >= 360);
        Assert.True(usage.Total.CostMicros >= 3_000);
        Assert.True(usage.BySource.Single(row => row.Source == "gateway").Chars >= 120);
        Assert.True(usage.BySource.Single(row => row.Source == "speech").Chars >= 240);
        var today = Assert.Single(usage.ByDay);
        Assert.True(today.Requests >= 2);
        Assert.True(usage.Today.CostMicros >= 3_000);
        Assert.True(usage.Today.BudgetMicros > 0);

        var invalid = await AdminAsync(_client, HttpMethod.Get, "/api/v1/admin/usage?days=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Admin_accounts_supports_prefix_search_pagination_and_filters()
    {
        var prefix = $"acct-{Guid.NewGuid():N}";
        var firstEmail = $"{prefix}-a@example.com";
        var secondEmail = $"{prefix}-b@example.com";
        var thirdEmail = $"{prefix}-c@example.com";
        await RegisterAsync(_client, firstEmail);
        await RegisterAsync(_client, secondEmail);
        await RegisterAsync(_client, thirdEmail);

        using (var upgrade = await AdminAsync(_client, HttpMethod.Post,
                   $"/api/v1/admin/accounts/{secondEmail}/plan", new PlanChangeRequest("standard")))
        {
            upgrade.EnsureSuccessStatusCode();
        }

        using (var disable = await AdminAsync(_client, HttpMethod.Post,
                   $"/api/v1/admin/accounts/{thirdEmail}/disable"))
        {
            disable.EnsureSuccessStatusCode();
        }

        var response = await AdminAsync(_client, HttpMethod.Get,
            $"/api/v1/admin/accounts?query={Uri.EscapeDataString(prefix)}&limit=10");
        response.EnsureSuccessStatusCode();
        var page = (await response.Content.ReadFromJsonAsync<AdminAccountPageResponse>())!;
        Assert.Equal(3, page.Total);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, item => Assert.StartsWith(prefix, item.Email));
        var standard = page.Items.Single(item => item.Email == secondEmail);
        Assert.Equal("standard", standard.Plan);
        Assert.Equal("standard", standard.EffectivePlan);
        Assert.True(page.Items.Single(item => item.Email == thirdEmail).Disabled);

        var firstPage = await AdminAsync(_client, HttpMethod.Get,
            $"/api/v1/admin/accounts?query={Uri.EscapeDataString(prefix)}&limit=1&offset=0");
        var secondPage = await AdminAsync(_client, HttpMethod.Get,
            $"/api/v1/admin/accounts?query={Uri.EscapeDataString(prefix)}&limit=1&offset=1");
        var first = (await firstPage.Content.ReadFromJsonAsync<AdminAccountPageResponse>())!;
        var second = (await secondPage.Content.ReadFromJsonAsync<AdminAccountPageResponse>())!;
        Assert.Equal(3, first.Total);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Email, second.Items[0].Email);

        var capped = await AdminAsync(_client, HttpMethod.Get, "/api/v1/admin/accounts?limit=500");
        capped.EnsureSuccessStatusCode();
        var cappedPage = (await capped.Content.ReadFromJsonAsync<AdminAccountPageResponse>())!;
        Assert.Equal(200, cappedPage.Limit);

        // % 必须被转义成字面量，不能变成「查所有」。
        var wildcard = await AdminAsync(_client, HttpMethod.Get, "/api/v1/admin/accounts?query=%25");
        wildcard.EnsureSuccessStatusCode();
        var wildcardPage = (await wildcard.Content.ReadFromJsonAsync<AdminAccountPageResponse>())!;
        Assert.Equal(0, wildcardPage.Total);
    }

    [Fact]
    public async Task Admin_disable_blocks_existing_tokens_and_enable_restores_login()
    {
        var email = UniqueEmail();
        var auth = await RegisterAsync(_client, email);

        using (var disable = await AdminAsync(_client, HttpMethod.Post,
                   $"/api/v1/admin/accounts/{email}/disable"))
        {
            disable.EnsureSuccessStatusCode();
        }

        // 幂等：再禁一次仍然是 200。
        using (var again = await AdminAsync(_client, HttpMethod.Post,
                   $"/api/v1/admin/accounts/{email}/disable"))
        {
            again.EnsureSuccessStatusCode();
        }

        using (var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me"))
        {
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(me)).StatusCode);
        }

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", "fp-after-disable", "pc"));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("account_disabled", (await login.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        using (var enable = await AdminAsync(_client, HttpMethod.Post,
                   $"/api/v1/admin/accounts/{email}/enable"))
        {
            enable.EnsureSuccessStatusCode();
        }

        var restored = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", "fp-after-enable", "pc"));
        restored.EnsureSuccessStatusCode();

        var missing = await AdminAsync(_client, HttpMethod.Post,
            $"/api/v1/admin/accounts/missing-{Guid.NewGuid():N}@example.com/disable");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_require_token()
    {
        var response = await _client.GetAsync("/api/v1/admin/usage");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_diagnostics_reports_database_and_counters()
    {
        var response = await AdminAsync(_client, HttpMethod.Get, "/api/v1/admin/diagnostics");
        response.EnsureSuccessStatusCode();
        var diagnostics = (await response.Content.ReadFromJsonAsync<AdminDiagnosticsResponse>())!;

        Assert.True(diagnostics.StartedAtUnix > 0);
        Assert.True(diagnostics.UptimeSeconds >= 0);
        Assert.True(diagnostics.Database.SizeBytes > 0);
        Assert.True(diagnostics.Gateway.Requests >= 0);
        Assert.NotNull(diagnostics.Gateway.ByReason);
    }

    [Fact]
    public async Task Admin_rate_limit_blocks_after_the_configured_limit()
    {
        using var factory = new AdminRateLimitFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK,
            (await AdminAsync(client, HttpMethod.Get, "/api/v1/admin/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await AdminAsync(client, HttpMethod.Get, "/api/v1/admin/metrics")).StatusCode);
    }

    private sealed class AdminRateLimitFactory : ServerFactory
    {
        protected override int AdminRequestsPerMinute => 1;
    }
}