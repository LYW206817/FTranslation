using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FastTranslation.Core;
using FastTranslation.Core.Account;
using Xunit;

namespace FastTranslation.Core.Tests;

public class AccountClientTests
{
    private const string AuthJson = """
    {
      "accessToken": "access-1",
      "refreshToken": "refresh-1",
      "expiresInSeconds": 1800,
      "account": { "id": "8f1a2c3d-0000-4000-8000-000000000001", "email": "a@b.test", "plan": "light", "quotaMonthly": 50000, "quotaDaily": null },
      "usage": { "usedMonthlyChars": 1200, "usedDailyChars": null, "quotaMonthlyChars": 50000, "quotaDailyChars": null, "remainingMonthlyChars": 48800, "remainingDailyChars": null, "periodStart": "2026-10-05", "day": "2026-10-28" }
    }
    """;

    private const string UsageJson = """
    {
      "usedMonthlyChars": 9000, "usedDailyChars": 500, "quotaMonthlyChars": 10000,
      "quotaDailyChars": 2000, "remainingMonthlyChars": 1000, "remainingDailyChars": 1500,
      "periodStart": "2026-10-05", "day": "2026-10-28"
    }
    """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public StubHandler(HttpStatusCode status, string body, Exception? toThrow = null)
        {
            _status = status;
            _body = body;
            _throw = toThrow;
        }

        public Uri? LastUri { get; private set; }
        public string LastMethod { get; private set; } = string.Empty;
        public string LastBody { get; private set; } = string.Empty;
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public string? LastDeviceHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization;
            LastDeviceHeader = request.Headers.TryGetValues("X-FT-Device", out var deviceValues)
                ? deviceValues.FirstOrDefault()
                : null;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_throw is not null) throw _throw;
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private static (AccountApiClient Client, StubHandler Handler) Build(
        HttpStatusCode status, string body, Exception? toThrow = null, string? baseUrl = null)
    {
        var handler = new StubHandler(status, body, toThrow);
        return (new AccountApiClient(new HttpClient(handler), baseUrl), handler);
    }

    [Theory]
    [InlineData(null, AccountApiClient.DefaultBaseUrl)]
    [InlineData("", AccountApiClient.DefaultBaseUrl)]
    [InlineData("   ", AccountApiClient.DefaultBaseUrl)]
    [InlineData(" https://api.example.com/ ", "https://api.example.com")]
    [InlineData("https://api.example.com", "https://api.example.com")]
    public void NormalizeBaseUrl_Falls_back_or_trims(string? input, string expected) =>
        Assert.Equal(expected, AccountApiClient.NormalizeBaseUrl(input));

    /// <summary>
    /// 默认地址由构建配置决定：Release 包必须指向备案域名，Debug 留在本机。
    /// 这里把两个常量都钉住，改名或降级成 http 时测试先失败，避免发布包把用户连到解析不到的地址。
    /// </summary>
    [Fact]
    public void DefaultBaseUrl_follows_the_build_configuration()
    {
        Assert.Equal("https://ftranslation.cn", AccountApiClient.ProductionBaseUrl);
        Assert.Equal(Uri.UriSchemeHttps, new Uri(AccountApiClient.ProductionBaseUrl).Scheme);
        Assert.Equal("127.0.0.1", new Uri(AccountApiClient.DevelopmentBaseUrl).Host);

#if DEBUG
        Assert.Equal(AccountApiClient.DevelopmentBaseUrl, AccountApiClient.DefaultBaseUrl);
#else
        Assert.Equal(AccountApiClient.ProductionBaseUrl, AccountApiClient.DefaultBaseUrl);
#endif
    }

    [Fact]
    public async Task Register_Posts_to_the_relative_auth_path_and_maps_the_session()
    {
        var (client, handler) = Build(HttpStatusCode.OK, AuthJson, baseUrl: "https://api.example.com/");

        var result = await client.RegisterAsync("a@b.test", "password123", "123456", "fp", "PC", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/auth/register", handler.LastUri!.ToString());
        Assert.Equal("POST", handler.LastMethod);
        Assert.Null(handler.LastAuthorization);
        // 服务端契约里这个字段叫 “code”;请求体必须与它完全一致
        Assert.Contains("\"code\":\"123456\"", handler.LastBody);

        var session = result.Value!.Session;
        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal("light", session.Profile.Plan);
        Assert.Equal(50000, session.Profile.QuotaMonthly);
        Assert.Equal(1200, result.Value.Usage.UsedMonthlyChars);
        Assert.Equal("2026-10-05", result.Value.Usage.PeriodStart);
    }

    [Fact]
    public async Task Login_sends_the_error_code_the_server_returned()
    {
        var (client, _) = Build(HttpStatusCode.Unauthorized, """{"error":"credentials_invalid"}""");

        var result = await client.LoginAsync("a@b.test", "nope", "fp", "PC", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountErrorCode.InvalidCredentials, result.Error!.Code);
    }

    [Fact]
    public async Task Duplicate_email_surfaces_as_EmailTaken()
    {
        var (client, _) = Build(HttpStatusCode.Conflict, """{"error":"email_taken"}""");

        var result = await client.RegisterAsync("a@b.test", "password123", null, "fp", "PC", CancellationToken.None);

        Assert.Equal(AccountErrorCode.EmailTaken, result.Error!.Code);
    }

    [Theory]
    [InlineData("email_invalid")]
    [InlineData("email_required")]
    [InlineData("email_too_long")]
    public async Task Email_format_errors_surface_as_EmailInvalid(string code)
    {
        // 不映射的话会落到 Server,用户填错邮箱却看到「账号服务器出错了」。
        var (client, _) = Build(HttpStatusCode.BadRequest, "{\"error\":\"" + code + "\"}");

        var result = await client.RegisterAsync("a@b.test", "password123", null, "fp", "PC", CancellationToken.None);

        Assert.Equal(AccountErrorCode.EmailInvalid, result.Error!.Code);
    }

    [Theory]
    [InlineData("code_invalid")]
    [InlineData("code_expired")]
    [InlineData("code_missing")]
    [InlineData("code_attempts_exceeded")]
    public async Task Verification_codes_collapse_to_one_error(string code)
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, "{\"error\":\"" + code + "\"}");

        var result = await client.RegisterAsync("a@b.test", "password123", "000000", "fp", "PC", CancellationToken.None);

        Assert.Equal(AccountErrorCode.Verification, result.Error!.Code);
    }

    [Fact]
    public async Task Status_without_a_known_code_falls_back_to_the_http_status()
    {
        var rateLimited = Build(HttpStatusCode.TooManyRequests, "{}");
        var unauthorized = Build(HttpStatusCode.Unauthorized, "not json at all");

        var a = await rateLimited.Client.GetProfileAsync("t", CancellationToken.None);
        var b = await unauthorized.Client.GetProfileAsync("t", CancellationToken.None);

        Assert.Equal(AccountErrorCode.RateLimited, a.Error!.Code);
        Assert.Equal(AccountErrorCode.Unauthorized, b.Error!.Code);
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_Network_not_thrown()
    {
        var (client, _) = Build(HttpStatusCode.OK, "{}", new HttpRequestException("connection refused"));

        var result = await client.GetUsageAsync("t", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountErrorCode.Network, result.Error!.Code);
    }

    [Fact]
    public async Task Timeout_is_reported_as_Network()
    {
        var (client, _) = Build(HttpStatusCode.OK, "{}", new TaskCanceledException("timed out"));

        var result = await client.RefreshAsync("r", CancellationToken.None);

        Assert.Equal(AccountErrorCode.Network, result.Error!.Code);
    }

    [Fact]
    public async Task Malformed_body_is_InvalidResponse()
    {
        var (client, _) = Build(HttpStatusCode.OK, "<html>gateway</html>");

        var result = await client.GetProfileAsync("t", CancellationToken.None);

        Assert.Equal(AccountErrorCode.InvalidResponse, result.Error!.Code);
    }

    [Fact]
    public async Task Authenticated_calls_carry_the_bearer_token()
    {
        // v112: 显式传 baseUrl。默认地址按 #if DEBUG 分档(v107)，不传会让这组 URI 断言在 Release 下失败。
        var (client, handler) = Build(HttpStatusCode.OK, UsageJson, baseUrl: AccountApiClient.DevelopmentBaseUrl);

        var result = await client.GetUsageAsync("access-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("http://127.0.0.1:5199/api/v1/account/usage", handler.LastUri!.ToString());
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Equal("access-1", handler.LastAuthorization.Parameter);
        Assert.Equal(9000, result.Value!.UsedMonthlyChars);
        Assert.Equal(2000, result.Value.QuotaDailyChars);
    }

    [Fact]
    public async Task Usage_asks_for_this_device_when_a_fingerprint_is_known()
    {
        var (client, handler) = Build(HttpStatusCode.OK, UsageJson, baseUrl: AccountApiClient.DevelopmentBaseUrl);

        var result = await client.GetUsageAsync("access-1", CancellationToken.None, "fp-1");

        Assert.True(result.Succeeded);
        Assert.Equal("http://127.0.0.1:5199/api/v1/account/usage?fingerprint=fp-1", handler.LastUri!.ToString());
    }

    private const string DevicesJson = """
    {
      "devices": [
        { "id": "11111111-1111-4111-8111-111111111111", "name": "QA-PC-1",
          "createdAt": 1790000000, "lastSeenAt": 1800000000, "isCurrent": false },
        { "id": "22222222-2222-4222-8222-222222222222", "name": "REAL-MACHINE",
          "createdAt": 1790000001, "lastSeenAt": 1800000001, "isCurrent": true }
      ],
      "maxDevices": 3
    }
    """;

    [Fact]
    public async Task Devices_are_listed_with_the_calling_fingerprint_in_the_header()
    {
        var (client, handler) = Build(HttpStatusCode.OK, DevicesJson, baseUrl: AccountApiClient.DevelopmentBaseUrl);

        var result = await client.GetDevicesAsync("access-1", "fp-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("http://127.0.0.1:5199/api/v1/account/devices", handler.LastUri!.ToString());
        Assert.Equal("fp-1", handler.LastDeviceHeader);
        Assert.Equal(3, result.Value!.MaxDevices);
        Assert.Equal(2, result.Value.Devices.Count);
        Assert.True(result.Value.Devices[1].IsCurrent);
        // 当前设备的机器名永远不显示，统一叫「这台电脑」
        Assert.Equal("这台电脑", result.Value.Devices[1].DisplayName);
        Assert.Equal("QA-PC-1", result.Value.Devices[0].DisplayName);
    }

    [Fact]
    public async Task Removing_a_device_uses_DELETE_and_the_device_header()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var (client, handler) = Build(HttpStatusCode.OK, """
            { "id": "11111111-1111-4111-8111-111111111111", "name": "QA-PC-1",
              "createdAt": 1790000000, "lastSeenAt": 1800000000, "isCurrent": false }
            """, baseUrl: AccountApiClient.DevelopmentBaseUrl);

        var result = await client.RemoveDeviceAsync("access-1", id, "fp-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("DELETE", handler.LastMethod);
        Assert.Equal("http://127.0.0.1:5199/api/v1/account/devices/" + id.ToString("D"),
            handler.LastUri!.ToString());
        Assert.Equal("fp-1", handler.LastDeviceHeader);
    }

    [Theory]
    [InlineData("device_is_current", AccountErrorCode.DeviceIsCurrent)]
    [InlineData("device_missing", AccountErrorCode.DeviceMissing)]
    public async Task Device_errors_map_to_their_codes(string code, AccountErrorCode expected)
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, "{\"error\":\"" + code + "\"}");

        var result = await client.RemoveDeviceAsync("access-1", Guid.NewGuid(), "fp", CancellationToken.None);

        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public async Task Device_limit_surfaces_as_DeviceLimit()
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"error":"device_limit_reached"}""");

        var result = await client.LoginAsync("a@b.test", "password123", "fp", "PC", CancellationToken.None);

        Assert.Equal(AccountErrorCode.DeviceLimit, result.Error!.Code);
    }

    [Fact]
    public void Device_last_seen_is_relative_and_never_negative()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.Equal("刚刚", AccountDevice.DescribeLastSeen(now.ToUnixTimeSeconds() + 30, now));
        Assert.Equal("5 分钟前", AccountDevice.DescribeLastSeen(now.ToUnixTimeSeconds() - 300, now));
        Assert.Equal("2 小时前", AccountDevice.DescribeLastSeen(now.ToUnixTimeSeconds() - 7_200, now));
        Assert.Equal("3 天前", AccountDevice.DescribeLastSeen(now.ToUnixTimeSeconds() - (3 * 86_400), now));
    }

    [Fact]
    public async Task Verification_disabled_means_the_form_skips_the_code()
    {
        var (client, _) = Build(HttpStatusCode.OK, """{"message":"verification_disabled"}""");

        var result = await client.RequestVerificationCodeAsync("a@b.test", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.VerificationRequired);
    }

    [Fact]
    public async Task Sent_code_means_the_form_asks_for_it()
    {
        var (client, _) = Build(HttpStatusCode.OK, """{"message":"sent"}""");

        var result = await client.RequestVerificationCodeAsync("a@b.test", CancellationToken.None);

        Assert.True(result.Value!.VerificationRequired);
    }

    [Fact]
    public async Task Logout_swallows_a_dead_server()
    {
        var (client, _) = Build(HttpStatusCode.OK, "{}", new HttpRequestException("offline"));

        var result = await client.LogoutAsync("t", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountErrorCode.Network, result.Error!.Code);
    }
    private const string RedeemJson = """
    {
      "account": { "id": "8f1a2c3d-0000-4000-8000-000000000001", "email": "a@b.test", "plan": "standard",
                   "quotaMonthly": 200000, "quotaDaily": null, "planExpiresAt": 1798761600 },
      "usage": { "usedMonthlyChars": 0, "usedDailyChars": null, "quotaMonthlyChars": 200000, "quotaDailyChars": null,
                 "remainingMonthlyChars": 200000, "remainingDailyChars": null, "periodStart": "2026-10-05", "day": "2026-10-28" }
    }
    """;

    [Fact]
    public async Task Redeem_posts_the_code_and_maps_the_new_plan()
    {
        var (client, handler) = Build(HttpStatusCode.OK, RedeemJson, baseUrl: "https://api.example.com");

        var result = await client.RedeemAsync("access-1", "FT-4G7M-2XQP-9HKV-3WTA", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/redeem", handler.LastUri!.ToString());
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Contains("FT-4G7M-2XQP-9HKV-3WTA", handler.LastBody);
        Assert.Equal("standard", result.Value!.Profile.Plan);
        Assert.Equal(1798761600, result.Value.Profile.PlanExpiresAtUnix);
        Assert.False(result.Value.Usage.HasDaily);
    }

    [Theory]
    [InlineData("redeem_code_invalid", AccountErrorCode.CodeInvalid)]
    [InlineData("redeem_code_used", AccountErrorCode.CodeUsed)]
    [InlineData("redeem_code_revoked", AccountErrorCode.CodeRevoked)]
    [InlineData("redeem_plan_downgrade", AccountErrorCode.PlanDowngrade)]
    public async Task Redeem_errors_are_not_confused_with_the_email_verification_codes(
        string wire, AccountErrorCode expected)
    {
        // “code_invalid” 已经表示“邮箱验证码错误”;兑换码必须有自己的名字。
        var (client, _) = Build(HttpStatusCode.BadRequest, "{\"error\":\"" + wire + "\"}");

        var result = await client.RedeemAsync("access-1", "FT-4G7M-2XQP-9HKV-3WTA", CancellationToken.None);

        Assert.Equal(expected, result.Error!.Code);
    }
    private const string AccountJson = """
    { "id": "8f1a2c3d-0000-4000-8000-000000000001", "email": "a@b.test", "plan": "light",
      "quotaMonthly": 50000, "quotaDaily": null, "planExpiresAt": null, "displayName": "永威" }
    """;

    [Fact]
    public async Task UpdateProfile_patches_the_profile_path_with_the_new_name()
    {
        var (client, handler) = Build(HttpStatusCode.OK, AccountJson, baseUrl: "https://api.example.com/");

        var result = await client.UpdateProfileAsync("access-1", "永威", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/account/profile", handler.LastUri!.ToString());
        Assert.Equal("PATCH", handler.LastMethod);
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        // 契约字段是 displayName;空字符串是界面清空名称的方式
        using var sent = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("永威", sent.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("永威", result.Value!.DisplayName);
        Assert.Equal("永威", result.Value.DisplayNameOrEmail);
    }

    [Fact]
    public async Task UpdateProfile_surfaces_the_server_rejection()
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"error":"display_name_too_long"}""");

        var result = await client.UpdateProfileAsync("access-1", new string('x', 25), CancellationToken.None);

        Assert.False(result.Succeeded);
        // 账号页会按每个 code 展示一句话,所以线上返回的 code 必须能穿过映射
        Assert.Equal(AccountErrorCode.InvalidProfile, result.Error!.Code);
    }

    [Fact]
    public async Task ChangePassword_posts_every_field_the_server_needs()
    {
        var (client, handler) = Build(HttpStatusCode.OK, AuthJson, baseUrl: "https://api.example.com/");

        var result = await client.ChangePasswordAsync(
            "access-1", "password123", "brand-new-123", "fp-1", "PC", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/account/password", handler.LastUri!.ToString());
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("\"currentPassword\":\"password123\"", handler.LastBody);
        Assert.Contains("\"newPassword\":\"brand-new-123\"", handler.LastBody);
        // 改了密码的设备会拿到一对新令牌,而不是 401
        Assert.Equal("refresh-1", result.Value!.Session.RefreshToken);
    }

    [Fact]
    public async Task ChangePassword_maps_a_wrong_current_password()
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"error":"password_mismatch"}""");

        var result = await client.ChangePasswordAsync(
            "access-1", "nope", "brand-new-123", "fp-1", "PC", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountErrorCode.PasswordMismatch, result.Error!.Code);
    }

    [Fact]
    public async Task RequestPasswordResetCode_posts_to_the_reset_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"message":"code_sent"}""",
            baseUrl: "https://api.example.com/");

        var result = await client.RequestPasswordResetCodeAsync("a@b.test", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/auth/password/reset/code", handler.LastUri!.ToString());
        Assert.Contains("\"email\":\"a@b.test\"", handler.LastBody);
    }

    [Fact]
    public async Task ResetPassword_posts_the_code_and_new_password()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"message":"password_reset"}""",
            baseUrl: "https://api.example.com/");

        var result = await client.ResetPasswordAsync(
            "a@b.test", "123456", "brand-new-123", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/auth/password/reset", handler.LastUri!.ToString());
        Assert.Contains("\"code\":\"123456\"", handler.LastBody);
        Assert.Contains("\"newPassword\":\"brand-new-123\"", handler.LastBody);
    }

    [Fact]
    public async Task DeleteAccount_sends_password_and_email_confirmation()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"message":"account_deleted"}""",
            baseUrl: "https://api.example.com/");

        var result = await client.DeleteAccountAsync(
            "access-1", "pw", "a@b.test", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/account/delete", handler.LastUri!.ToString());
        Assert.Equal("Bearer access-1", handler.LastAuthorization?.ToString());
        Assert.Contains("\"confirmation\":\"a@b.test\"", handler.LastBody);
    }

    [Theory]
    [InlineData("confirmation_mismatch", AccountErrorCode.ConfirmationMismatch)]
    [InlineData("mail_unavailable", AccountErrorCode.MailUnavailable)]
    [InlineData("reset_invalid", AccountErrorCode.ResetInvalid)]
    public async Task Account_lifecycle_errors_map_to_their_codes(string code, AccountErrorCode expected)
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, $$"""{"error":"{{code}}"}""");

        var result = await client.ResetPasswordAsync(
            "a@b.test", "123456", "brand-new-123", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public async Task CreateOrder_posts_plan_and_cycle_and_maps_amount_and_qr_methods()
    {
        const string orderJson = """
        {
          "id": "8f1a2c3d-0000-4000-8000-000000000002",
          "plan": "standard", "cycle": "yearly", "days": 365, "amountCents": 11900,
          "status": "pending_payment",
          "paymentMethods": [
            { "method": "wechat", "label": "微信", "qrDataUrl": "data:image/png;base64,AAAA" },
            { "method": "alipay", "label": "支付宝", "qrDataUrl": "data:image/png;base64,BBBB" }
          ],
          "paymentMethod": "", "paymentRef": "", "createdAt": 1794450000,
          "submittedAt": null, "confirmedAt": null, "cancelledAt": null, "cancelReason": ""
        }
        """;
        var (client, handler) = Build(HttpStatusCode.OK, orderJson, baseUrl: "https://api.example.com");

        var result = await client.CreateOrderAsync("token", "standard", "yearly", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/orders", handler.LastUri!.ToString());
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("\"plan\":\"standard\"", handler.LastBody);
        Assert.Contains("\"cycle\":\"yearly\"", handler.LastBody);
        Assert.Equal("Bearer token", handler.LastAuthorization?.ToString());
        Assert.Equal(365, result.Value!.Days);
        Assert.Equal("¥119.00", result.Value.AmountDisplay);
        Assert.Equal(2, result.Value.PaymentMethods.Count);
        Assert.Equal("Pro", result.Value.PlanDisplay);
    }

    [Fact]
    public async Task Missing_current_order_maps_to_OrderMissing()
    {
        var (client, _) = Build(HttpStatusCode.NotFound, """{"error":"order_missing"}""");

        var result = await client.GetCurrentOrderAsync("token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountErrorCode.OrderMissing, result.Error!.Code);
    }

    [Fact]
    public async Task SubmitOrder_sends_the_full_transaction_number()
    {
        const string orderJson = """
        {
          "id": "8f1a2c3d-0000-4000-8000-000000000003",
          "plan": "light", "cycle": "monthly", "days": 30, "amountCents": 690,
          "status": "pending_review",
          "paymentMethods": [],
          "paymentMethod": "wechat", "paymentRef": "420000123456789012345678",
          "createdAt": 1794450000, "submittedAt": 1794450100,
          "confirmedAt": null, "cancelledAt": null, "cancelReason": ""
        }
        """;
        var (client, handler) = Build(HttpStatusCode.OK, orderJson);

        var result = await client.SubmitOrderAsync(
            "token", Guid.Parse("8f1a2c3d-0000-4000-8000-000000000003"),
            "wechat", "420000123456789012345678", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("\"paymentRef\":\"420000123456789012345678\"", handler.LastBody);
        Assert.Equal("wechat", result.Value!.PaymentMethod);
        Assert.Equal("等待人工确认", result.Value.StatusDisplay);
    }

    [Theory]
    [InlineData("payment_ref_used", AccountErrorCode.PaymentRefUsed)]
    [InlineData("payment_unavailable", AccountErrorCode.PaymentUnavailable)]
    [InlineData("upgrade_requires_support", AccountErrorCode.UpgradeRequiresSupport)]
    [InlineData("order_pending", AccountErrorCode.OrderPending)]
    public async Task Order_errors_map_to_actionable_codes(string wire, AccountErrorCode expected)
    {
        var (client, _) = Build(HttpStatusCode.Conflict, "{\"error\":\"" + wire + "\"}");

        var result = await client.CreateOrderAsync("token", "standard", "monthly", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Error!.Code);
    }
}

public class AccountSessionStoreTests
{
    private static AccountSession Sample(DateTimeOffset expires) => new(
        "access", "refresh", expires, new AccountProfile(Guid.NewGuid(), "a@b.test", "light", 50000, null));

    [Fact]
    public void Round_trips_through_the_secret_store()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        var session = Sample(DateTimeOffset.UtcNow.AddMinutes(20));

        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("access", loaded!.AccessToken);
        Assert.Equal("refresh", loaded.RefreshToken);
        Assert.Equal(session.Profile.Id, loaded.UserId);
        Assert.Equal("light", loaded.Plan);
        Assert.Equal(session.ExpiresAt.ToUnixTimeSeconds(), loaded.ExpiresAtUnix);
    }

    [Fact]
    public void Corrupt_blob_reads_as_never_signed_in()
    {
        var secrets = new InMemorySecretStore();
        secrets.SaveSecret(AccountSessionStore.SecretName, "{not json");

        Assert.Null(new AccountSessionStore(secrets).Load());
    }

    [Fact]
    public void Missing_secret_reads_as_never_signed_in() =>
        Assert.Null(new AccountSessionStore(new InMemorySecretStore()).Load());

    [Fact]
    public void Clear_removes_the_secret()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        store.Save(Sample(DateTimeOffset.UtcNow.AddMinutes(20)));

        store.Clear();

        Assert.Null(store.Load());
        Assert.Null(secrets.LoadSecret(AccountSessionStore.SecretName));
    }

    [Fact]
    public void Expiry_is_pulled_forward_by_a_minute()
    {
        var now = DateTimeOffset.UtcNow;
        var boundary = new StoredSession("a", "r", now.AddSeconds(59).ToUnixTimeSeconds(), Guid.NewGuid(), "e", "free");
        var roomy = new StoredSession("a", "r", now.AddSeconds(61).ToUnixTimeSeconds(), Guid.NewGuid(), "e", "free");

        Assert.True(boundary.IsExpired(now));
        Assert.False(roomy.IsExpired(now));
    }
}

public class DeviceIdentityTests
{
    [Fact]
    public void Reuses_the_id_it_created()
    {
        var secrets = new InMemorySecretStore();

        var first = DeviceIdentity.LoadOrCreate(secrets);
        var second = DeviceIdentity.LoadOrCreate(secrets);

        Assert.Equal(first, second);
        Assert.True(Guid.TryParse(first, out _));
    }

    [Fact]
    public void Replaces_a_corrupt_stored_id()
    {
        var secrets = new InMemorySecretStore();
        secrets.SaveSecret(DeviceIdentity.SecretName, "not-a-guid");

        var id = DeviceIdentity.LoadOrCreate(secrets);

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, secrets.LoadSecret(DeviceIdentity.SecretName));
    }

    [Fact]
    public void Machine_name_is_never_empty() => Assert.False(string.IsNullOrWhiteSpace(DeviceIdentity.MachineName));
}

public class AccountUsageTests
{
    private static AccountUsage Usage(int usedMonthly, int quotaMonthly, int? usedDaily = null, int? quotaDaily = null) =>
        new(usedMonthly, usedDaily, quotaMonthly, quotaDaily,
            Math.Max(0, quotaMonthly - usedMonthly),
            quotaDaily is { } qd ? Math.Max(0, qd - (usedDaily ?? 0)) : null,
            "2026-10", "2026-10-28");

    [Fact]
    public void Fractions_are_clamped_to_one()
    {
        var usage = Usage(20000, 10000, 5000, 2000);

        Assert.Equal(1.0, usage.MonthlyFraction);
        Assert.Equal(1.0, usage.DailyFraction);
    }

    [Fact]
    public void Paid_plans_have_no_daily_bar()
    {
        var usage = Usage(1000, 200000);

        Assert.False(usage.HasDaily);
        Assert.Equal(0, usage.DailyFraction);
    }

    [Fact]
    public void Zero_quota_never_divides_by_zero()
    {
        var usage = Usage(0, 0, 0, 0);

        Assert.Equal(0, usage.MonthlyFraction);
        Assert.Equal(0, usage.DailyFraction);
    }

    [Fact]
    public void Half_used_month_reads_as_a_half_bar() =>
        Assert.Equal(0.5, Usage(5000, 10000).MonthlyFraction, 3);

    [Theory]
    [InlineData("free", "免费版")]
    [InlineData("light", "Lite")]
    [InlineData("standard", "Pro")]
    [InlineData("max", "Max")]
    [InlineData("weird", "免费版")]
    public void Plan_names_are_user_facing(string plan, string expected) =>
        Assert.Equal(expected, new AccountProfile(Guid.Empty, "e", plan, 0, null).PlanDisplay);

    [Fact]
    public void Error_codes_carry_a_chinese_sentence() =>
        Assert.Contains("网络", new AccountError(AccountErrorCode.Network, "raw").Display);



    [Fact]
    public void Email_format_error_reads_as_a_typo_hint() =>
        Assert.Contains("邮箱", new AccountError(AccountErrorCode.EmailInvalid, "raw").Display);


    [Fact]
    public void Paid_plans_show_an_end_date_and_the_free_plan_does_not()
    {
        var paid = new AccountProfile(Guid.Empty, "e", "light", 50000, null, 1798761600);
        Assert.StartsWith("有效期至 ", paid.ExpiryLine, StringComparison.Ordinal);
        Assert.Null(new AccountProfile(Guid.Empty, "e", "light", 50000, null).ExpiryLine);
        Assert.Null(new AccountProfile(Guid.Empty, "e", "free", 10000, 2000, 1798761600).ExpiryLine);
    }
}
