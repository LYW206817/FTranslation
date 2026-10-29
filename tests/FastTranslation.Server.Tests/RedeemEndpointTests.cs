using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// The activation code is how money reaches an account without a payment provider: the operator
/// mints a batch, the buyer pastes one code. What must hold is that a code pays out exactly once.
/// </summary>
public class RedeemEndpointTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public RedeemEndpointTests(ServerFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            $"redeem-{Guid.NewGuid():N}@example.com", "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<string> MintAsync(PlanTier plan, int days = 30)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/codes")
        {
            Content = JsonContent.Create(new CreateCodesRequest(Plans.ToWire(plan), days, 1, "test")),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreateCodesResponse>())!;
        return created.Codes[0];
    }

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/api/v1/redeem", new RedeemRequest(code));

    private static async Task SetPlanAsync(HttpClient client, string email, PlanTier plan, int days)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
        {
            Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), days)),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Redeem_RequiresAuthentication()
    {
        using var client = _factory.CreateClient();
        var response = await RedeemAsync(client, "FT-0000-0000-0000-0000");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Redeem_UpgradesFreeAccountAndDropsTheDailyCap()
    {
        var client = await SignedInAsync();
        var code = await MintAsync(PlanTier.Light);

        var response = await RedeemAsync(client, code);

        response.EnsureSuccessStatusCode();
        var redeemed = (await response.Content.ReadFromJsonAsync<RedeemResponse>())!;
        Assert.Equal("light", redeemed.Account.Plan);
        Assert.Equal(Plans.LightMonthlyChars, redeemed.Account.QuotaMonthly);
        Assert.Null(redeemed.Usage.QuotaDailyChars);
        Assert.NotNull(redeemed.Account.PlanExpiresAt);
        Assert.True(redeemed.Account.PlanExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Redeem_AcceptsWhatAHumanRetypes()
    {
        var client = await SignedInAsync();
        var code = await MintAsync(PlanTier.Light);

        // lower case, dashes stripped, and the FT prefix dropped: still the same code.
        var retyped = code.Replace("-", string.Empty, StringComparison.Ordinal)[2..].ToLowerInvariant();

        var response = await RedeemAsync(client, retyped);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Redeem_RejectsUnknownCode()
    {
        var client = await SignedInAsync();

        var response = await RedeemAsync(client, "FT-0000-0000-0000-0000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("redeem_code_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redeem_RejectsMalformedCode()
    {
        var client = await SignedInAsync();

        var response = await RedeemAsync(client, "hello");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redeem_code_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redeem_PaysOutOnlyOnce()
    {
        var code = await MintAsync(PlanTier.Standard);
        var first = await SignedInAsync();
        var second = await SignedInAsync();

        (await RedeemAsync(first, code)).EnsureSuccessStatusCode();
        var again = await RedeemAsync(second, code);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("redeem_code_used", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redeem_RefusesToDowngradeAndKeepsTheCodeUsable()
    {
        var client = await SignedInAsync();
        var email = (await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.Email;
        await SetPlanAsync(client, email, PlanTier.Standard, 30);
        var cheaper = await MintAsync(PlanTier.Light);

        var refused = await RedeemAsync(client, cheaper);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("redeem_plan_downgrade", await refused.Content.ReadAsStringAsync());

        // The refusal must not burn the code: somebody on a cheaper plan can still use it.
        var other = await SignedInAsync();
        (await RedeemAsync(other, cheaper)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Redeem_StacksDaysWhenTheTierMatches()
    {
        var client = await SignedInAsync();
        var email = (await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.Email;
        await SetPlanAsync(client, email, PlanTier.Light, 30);
        var before = (await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.PlanExpiresAt!.Value;

        var response = await RedeemAsync(client, await MintAsync(PlanTier.Light, 30));

        response.EnsureSuccessStatusCode();
        var after = (await response.Content.ReadFromJsonAsync<RedeemResponse>())!.Account.PlanExpiresAt!.Value;
        Assert.InRange(after - before, 30L * 86_400L - 60L, 30L * 86_400L + 60L);
    }
}
