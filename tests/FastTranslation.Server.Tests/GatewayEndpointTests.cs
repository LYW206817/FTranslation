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

    /// <summary>Sends a chat request the way the desktop client does: Bearer token + device header.</summary>
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
        // The client's own Bearer token must never be forwarded; the developer key replaces it.
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

        // The reported tokens must beat the character-based estimate, otherwise cost tracking is fiction.
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
        // Light has no daily cap, so this isolates the monthly gate.
        var client = await SignedInClientAsync(PlanTier.Light);

        for (var i = 0; i < 10; i++)
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

        // Neither request may reach DashScope — a refused request must never cost money.
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

        // The daily ledger is keyed by device, so read it through the store the server itself uses.
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

        // The client only knows its fingerprint, never the server-side device id.
        var response = await client.GetAsync($"/api/v1/account/usage?fingerprint={device}");
        response.EnsureSuccessStatusCode();
        var usage = (await response.Content.ReadFromJsonAsync<UsageDto>())!;
        Assert.Equal(1_200, usage.UsedDailyChars);
    }

    private static async Task<string> EmailAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!.Email;
    }
}

/// <summary>Mirrors <c>GatewayEndpoints.DeviceHeaderName</c> for the test side.</summary>
internal static class GatewayEndpointConstants
{
    public const string DeviceHeader = "X-FT-Device";
}
