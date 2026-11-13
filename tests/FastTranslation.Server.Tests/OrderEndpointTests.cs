using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// 订阅订单的核心不变量：金额只由服务端算；一个账号同时只有一笔活动订单；
/// 交易单号不能一单多投；确认开通是幂等的，不会重复加天数。
/// </summary>
public class OrderEndpointTests : IClassFixture<ServerFactory>, IClassFixture<OrderEndpointTests.NoPaymentFactory>
{
    private readonly ServerFactory _factory;
    private readonly NoPaymentFactory _noPayment;

    public OrderEndpointTests(ServerFactory factory, NoPaymentFactory noPayment)
    {
        _factory = factory;
        _noPayment = noPayment;
    }

    private async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(ServerFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        var email = $"order-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    private static Task<HttpResponseMessage> CreateOrderAsync(HttpClient client, string plan, string cycle) =>
        client.PostAsJsonAsync("/api/v1/orders", new CreateOrderRequest(plan, cycle));

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid id, string method, string reference) =>
        client.PostAsJsonAsync($"/api/v1/orders/{id}/submit", new SubmitOrderRequest(method, reference));

    private async Task<HttpResponseMessage> SetPlanAsync(string email, PlanTier plan, int days)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
        {
            Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), days)),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        using var client = _factory.CreateClient();
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        using var client = _factory.CreateClient();
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task CreateOrder_RequiresAuthentication()
    {
        using var client = _factory.CreateClient();
        var response = await CreateOrderAsync(client, "standard", "monthly");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_UsesServerPriceAndBothQrCodes()
    {
        var (client, _) = await RegisterAsync();

        var response = await CreateOrderAsync(client, "standard", "quarterly");

        response.EnsureSuccessStatusCode();
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("standard", order.Plan);
        Assert.Equal("quarterly", order.Cycle);
        Assert.Equal(90, order.Days);
        Assert.Equal(3_570, order.AmountCents);
        Assert.Equal("pending_payment", order.Status);
        Assert.Equal(2, order.PaymentMethods.Count);
        Assert.Contains(order.PaymentMethods, item => item.Method == "wechat" && item.QrDataUrl.StartsWith("data:image/png;base64,"));
        Assert.Contains(order.PaymentMethods, item => item.Method == "alipay");
    }

    [Fact]
    public async Task CreateOrder_RejectsASecondActiveOrder()
    {
        var (client, _) = await RegisterAsync();
        (await CreateOrderAsync(client, "light", "monthly")).EnsureSuccessStatusCode();

        var again = await CreateOrderAsync(client, "light", "quarterly");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("order_pending", await again.Content.ReadAsStringAsync());

        var current = await client.GetAsync("/api/v1/orders/current");
        current.EnsureSuccessStatusCode();
        var order = (await current.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("pending_payment", order.Status);
    }

    [Fact]
    public async Task CreateOrder_RejectsUpgradeAndDowngradeWhileActive()
    {
        var (client, auth) = await RegisterAsync();
        (await SetPlanAsync(auth.Account.Email, PlanTier.Light, 30)).EnsureSuccessStatusCode();

        var upgrade = await CreateOrderAsync(client, "standard", "monthly");
        Assert.Equal(HttpStatusCode.Conflict, upgrade.StatusCode);
        Assert.Contains("upgrade_requires_support", await upgrade.Content.ReadAsStringAsync());

        var renewal = await CreateOrderAsync(client, "light", "monthly");
        renewal.EnsureSuccessStatusCode();

        var downgradeClient = (await RegisterAsync()).Client;
        (await SetPlanAsync((await GetMeAsync(downgradeClient)).Email, PlanTier.Max, 30)).EnsureSuccessStatusCode();
        var downgrade = await CreateOrderAsync(downgradeClient, "light", "monthly");
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);
        Assert.Contains("plan_downgrade", await downgrade.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SubmitOrder_AcceptsAPastedTransactionNumberAndStoresDigits()
    {
        var (client, _) = await RegisterAsync();
        var created = (await (await CreateOrderAsync(client, "light", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;
        const string pasted = "交易单号：420000123456789012345678";
        const string digits = "420000123456789012345678";

        var response = await SubmitAsync(client, created.Id, "wechat", pasted);

        response.EnsureSuccessStatusCode();
        var submitted = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("pending_review", submitted.Status);
        Assert.Equal("wechat", submitted.PaymentMethod);
        Assert.Equal(digits, submitted.PaymentRef);
    }

    [Fact]
    public async Task SubmitOrder_RejectsTheSameTransactionNumberForTwoAccounts()
    {
        var (first, _) = await RegisterAsync();
        var (second, _) = await RegisterAsync();
        var firstOrder = (await (await CreateOrderAsync(first, "light", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;
        var secondOrder = (await (await CreateOrderAsync(second, "light", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;

        (await SubmitAsync(first, firstOrder.Id, "wechat", "420000777777777777")).EnsureSuccessStatusCode();
        var duplicate = await SubmitAsync(second, secondOrder.Id, "alipay", "交易单号 420000777777777777");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("payment_ref_used", await duplicate.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfirmOrder_IsIdempotentAndExtendsThePlanOnce()
    {
        var (client, _) = await RegisterAsync();
        var created = (await (await CreateOrderAsync(client, "standard", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;
        (await SubmitAsync(client, created.Id, "wechat", "420000111111111111")).EnsureSuccessStatusCode();

        var confirm = await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/confirm");
        confirm.EnsureSuccessStatusCode();

        var me = await GetMeAsync(client);
        Assert.Equal("standard", me.Plan);
        Assert.NotNull(me.PlanExpiresAt);
        var expiry = me.PlanExpiresAt;

        var again = await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/confirm");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var after = await GetMeAsync(client);
        Assert.Equal(expiry, after.PlanExpiresAt);
    }

    [Fact]
    public async Task ConfirmOrder_RejectsAfterTheAccountPlanWasChangedMeanwhile()
    {
        var (client, auth) = await RegisterAsync();
        var created = (await (await CreateOrderAsync(client, "standard", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;
        (await SubmitAsync(client, created.Id, "wechat", "420000555555555555")).EnsureSuccessStatusCode();

        // 运营在待审核期间手工把账号改成 Max（例如用户补差价升级）。
        // 再确认这张旧的 Pro 订单会把 users.plan 覆盖回 Pro，等于静默降档。
        (await SetPlanAsync(auth.Account.Email, PlanTier.Max, 30)).EnsureSuccessStatusCode();

        var confirm = await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/confirm");
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Contains("order_plan_mismatch", await confirm.Content.ReadAsStringAsync());

        var me = await GetMeAsync(client);
        Assert.Equal("max", me.Plan);
    }

    [Fact]
    public async Task ConfirmOrder_ExtendsAnExistingSubscriptionInsteadOfMovingTheAnchor()
    {
        var (client, auth) = await RegisterAsync();
        (await SetPlanAsync(auth.Account.Email, PlanTier.Light, 30)).EnsureSuccessStatusCode();
        var before = await GetMeAsync(client);

        var created = (await (await CreateOrderAsync(client, "light", "quarterly")).Content.ReadFromJsonAsync<OrderDto>())!;
        (await SubmitAsync(client, created.Id, "alipay", "420000222222222222")).EnsureSuccessStatusCode();
        (await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/confirm")).EnsureSuccessStatusCode();

        var after = await GetMeAsync(client);
        Assert.Equal(90L * 86_400L, after.PlanExpiresAt!.Value - before.PlanExpiresAt!.Value);
    }

    [Fact]
    public async Task AdminCancel_ClosesTheOrderAndBlocksConfirmation()
    {
        var (client, _) = await RegisterAsync();
        var created = (await (await CreateOrderAsync(client, "max", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;
        (await SubmitAsync(client, created.Id, "wechat", "420000333333333333")).EnsureSuccessStatusCode();

        var cancel = await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/cancel",
            new CancelOrderRequest("未收到款项"));
        cancel.EnsureSuccessStatusCode();

        var order = (await (await client.GetAsync($"/api/v1/orders/{created.Id}")).Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("cancelled", order.Status);
        Assert.Equal("未收到款项", order.CancelReason);

        var confirm = await AdminPostAsync($"/api/v1/admin/orders/{created.Id}/confirm");
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
    }

    [Fact]
    public async Task Order_IsOnlyVisibleToItsOwner()
    {
        var (owner, _) = await RegisterAsync();
        var (other, _) = await RegisterAsync();
        var created = (await (await CreateOrderAsync(owner, "light", "monthly")).Content.ReadFromJsonAsync<OrderDto>())!;

        var response = await other.GetAsync($"/api/v1/orders/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_ReturnsPaymentUnavailableWhenNoQrIsConfigured()
    {
        var (client, _) = await RegisterAsync(_noPayment);

        var response = await CreateOrderAsync(client, "light", "monthly");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("payment_unavailable", await response.Content.ReadAsStringAsync());
    }

    private static async Task<AccountDto> GetMeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    /// <summary>把两个收款码路径显式清空，验证下单接口不会假装能收款。</summary>
    public sealed class NoPaymentFactory : ServerFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:Payment:WeChatQrPath"] = string.Empty,
                ["Server:Payment:AlipayQrPath"] = string.Empty,
            }));
        }
    }
}
