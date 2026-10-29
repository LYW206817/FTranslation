using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>激活码是资金在没有支付服务商时进入账号的途径:运营方
/// 铸造一批码,买家粘贴一个。必须成立的是:一个码恰好兑现一次。</summary>
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

    private async Task<string> MintAsync(PlanTier plan, int days = 30) =>
        (await MintBatchAsync(plan, days, 1))[0];

    private async Task<IReadOnlyList<string>> MintBatchAsync(PlanTier plan, int days, int count)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/codes")
        {
            Content = JsonContent.Create(new CreateCodesRequest(Plans.ToWire(plan), days, count, "test")),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateCodesResponse>())!.Codes;
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

        // 小写、去掉连字符、再去掉 FT 前缀:仍然是同一个码。
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

        // 拒绝不能烧掉这个码:用更便宜套餐的人仍然可以用它。
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

    [Fact]
    public async Task Redeem_RefusesARevokedCode()
    {
        var client = await SignedInAsync();
        var code = await MintAsync(PlanTier.Light);

        (await RevokeAsync(code)).EnsureSuccessStatusCode();
        var response = await RedeemAsync(client, code);

        // 410 而不是 404:买家需要能够证明这个码真实存在且已被作废。
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("redeem_code_revoked", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redeem_UpgradeKeepsTheDaysAlreadyBought()
    {
        var client = await SignedInAsync();
        var email = (await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.Email;
        await SetPlanAsync(client, email, PlanTier.Light, 30);
        var before = (await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.PlanExpiresAt!.Value;

        var response = await RedeemAsync(client, await MintAsync(PlanTier.Standard, 30));

        response.EnsureSuccessStatusCode();
        var after = (await response.Content.ReadFromJsonAsync<RedeemResponse>())!;
        Assert.Equal(Plans.ToWire(PlanTier.Standard), after.Account.Plan);
        // 升档正是过去会白白丢掉已付费天数的场景:时钟必须从
        // 旧到期日按新码的天数前移,而不是从今天重新开始。
        Assert.True(after.Account.PlanExpiresAt > before, "an upgrade must extend the subscription");
        Assert.InRange(after.Account.PlanExpiresAt!.Value - before, 30L * 86_400L - 60L, 30L * 86_400L + 60L);
    }

    [Fact]
    public async Task Admin_ListsCodesByFate()
    {
        var codes = await MintBatchAsync(PlanTier.Light, 30, 2);
        var buyer = await SignedInAsync();
        var email = (await buyer.GetFromJsonAsync<AccountDto>("/api/v1/account/me"))!.Email;
        (await RedeemAsync(buyer, codes[0])).EnsureSuccessStatusCode();

        var open = await ReadPageAsync("open");
        var pending = Assert.Single(open.Items, row => row.Code == codes[1]);
        Assert.Equal("light", pending.Plan);
        Assert.Null(pending.RedeemedEmail);
        Assert.DoesNotContain(codes[0], open.Items.Select(row => row.Code));

        var used = await ReadPageAsync("used");
        var spent = Assert.Single(used.Items, row => row.Code == codes[0]);
        Assert.Equal(email, spent.RedeemedEmail);
        Assert.NotNull(spent.RedeemedAt);

        var paged = await ReadPageAsync("all&limit=1");
        Assert.Single(paged.Items);
        Assert.True(paged.Total >= 2, "the ledger counts every code, not just this page");
    }

    [Fact]
    public async Task Admin_ListRejectsAnUnknownStatus()
    {
        var response = await AdminSendAsync(HttpMethod.Get, "/api/v1/admin/codes?status=nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("status_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_RevokeIsIdempotentAndCannotClawBackSpentCodes()
    {
        var fresh = await MintAsync(PlanTier.Light);
        (await RevokeAsync(fresh)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(fresh)).StatusCode);

        // 已经兑现过的码是客户的财产:作废它会删掉
        // 他们买下的天数,而这正是这个账本绝不能做的事。
        var spent = await MintAsync(PlanTier.Light);
        (await RedeemAsync(await SignedInAsync(), spent)).EnsureSuccessStatusCode();
        var refused = await RevokeAsync(spent);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("code_already_redeemed", await refused.Content.ReadAsStringAsync());

        var missing = await RevokeAsync("FT-0000-0000-0000-0000");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("code_missing", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_CodeRoutesNeedTheAdminToken()
    {
        using var client = _factory.CreateClient();

        var listed = await client.GetAsync("/api/v1/admin/codes");
        var revoked = await client.PostAsync("/api/v1/admin/codes/FT-0000-0000-0000-0000/revoke", null);

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    private Task<HttpResponseMessage> RevokeAsync(string code) =>
        AdminSendAsync(HttpMethod.Post, $"/api/v1/admin/codes/{code}/revoke");

    private async Task<RedeemCodePageResponse> ReadPageAsync(string query)
    {
        using var response = await AdminSendAsync(HttpMethod.Get, "/api/v1/admin/codes?status=" + query);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"listing failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<RedeemCodePageResponse>())!;
    }

    private async Task<HttpResponseMessage> AdminSendAsync(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        return await _factory.CreateClient().SendAsync(request);
    }
}
