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
      "usage": { "usedMonthlyChars": 1200, "usedDailyChars": null, "quotaMonthlyChars": 50000, "quotaDailyChars": null, "remainingMonthlyChars": 48800, "remainingDailyChars": null, "month": "2026-10", "day": "2026-10-28" }
    }
    """;

    private const string UsageJson = """
    {
      "usedMonthlyChars": 9000, "usedDailyChars": 500, "quotaMonthlyChars": 10000,
      "quotaDailyChars": 2000, "remainingMonthlyChars": 1000, "remainingDailyChars": 1500,
      "month": "2026-10", "day": "2026-10-28"
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization;
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

    [Fact]
    public async Task Register_Posts_to_the_relative_auth_path_and_maps_the_session()
    {
        var (client, handler) = Build(HttpStatusCode.OK, AuthJson, baseUrl: "https://api.example.com/");

        var result = await client.RegisterAsync("a@b.test", "password123", "123456", "fp", "PC", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example.com/api/v1/auth/register", handler.LastUri!.ToString());
        Assert.Equal("POST", handler.LastMethod);
        Assert.Null(handler.LastAuthorization);
        // the server contract names the field "code"; the wire body must match it exactly
        Assert.Contains("\"code\":\"123456\"", handler.LastBody);

        var session = result.Value!.Session;
        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal("light", session.Profile.Plan);
        Assert.Equal(50000, session.Profile.QuotaMonthly);
        Assert.Equal(1200, result.Value.Usage.UsedMonthlyChars);
        Assert.Equal("2026-10", result.Value.Usage.Month);
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
        var (client, handler) = Build(HttpStatusCode.OK, UsageJson);

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
        var (client, handler) = Build(HttpStatusCode.OK, UsageJson);

        var result = await client.GetUsageAsync("access-1", CancellationToken.None, "fp-1");

        Assert.True(result.Succeeded);
        Assert.Equal("http://127.0.0.1:5199/api/v1/account/usage?fingerprint=fp-1", handler.LastUri!.ToString());
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
                 "remainingMonthlyChars": 200000, "remainingDailyChars": null, "month": "2026-10", "day": "2026-10-28" }
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
    [InlineData("redeem_plan_downgrade", AccountErrorCode.PlanDowngrade)]
    public async Task Redeem_errors_are_not_confused_with_the_email_verification_codes(
        string wire, AccountErrorCode expected)
    {
        // "code_invalid" already means "wrong email code"; redeem codes must carry their own names.
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
        // the contract field is displayName; an empty string is how the UI clears the name
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
        // the account page shows a sentence per code, so the wire code has to survive the mapping
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
        // the device that changed the password gets a fresh pair back, not a 401
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
    [InlineData("light", "轻享版")]
    [InlineData("standard", "标准版")]
    [InlineData("weird", "免费版")]
    public void Plan_names_are_user_facing(string plan, string expected) =>
        Assert.Equal(expected, new AccountProfile(Guid.Empty, "e", plan, 0, null).PlanDisplay);

    [Fact]
    public void Error_codes_carry_a_chinese_sentence() =>
        Assert.Contains("网络", new AccountError(AccountErrorCode.Network, "raw").Display);


    [Fact]
    public void Paid_plans_show_an_end_date_and_the_free_plan_does_not()
    {
        var paid = new AccountProfile(Guid.Empty, "e", "light", 50000, null, 1798761600);
        Assert.StartsWith("有效期至 ", paid.ExpiryLine, StringComparison.Ordinal);
        Assert.Null(new AccountProfile(Guid.Empty, "e", "light", 50000, null).ExpiryLine);
        Assert.Null(new AccountProfile(Guid.Empty, "e", "free", 10000, 2000, 1798761600).ExpiryLine);
    }
}
