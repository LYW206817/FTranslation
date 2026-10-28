using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FastTranslation.Server.Api;
using Xunit;

namespace FastTranslation.Server.Tests;

public class AuthEndpointTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointTests(ServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string email, string password = "password123")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_ReturnsTokensAndFreeQuota()
    {
        var email = UniqueEmail();
        var auth = await RegisterAsync(_client, email);

        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
        Assert.True(auth.ExpiresInSeconds > 0);
        Assert.Equal("Bearer", auth.TokenType);
        Assert.Equal(email, auth.Account.Email);
        Assert.Equal("free", auth.Account.Plan);
        Assert.Equal(10_000, auth.Account.QuotaMonthly);
        Assert.Equal(2_000, auth.Account.QuotaDaily);
        Assert.Equal(0, auth.Usage.UsedMonthlyChars);
        Assert.Equal(10_000, auth.Usage.RemainingMonthlyChars);
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmail()
    {
        var email = UniqueEmail();
        await RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-dup", "pc"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_taken", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Register_RejectsShortPassword()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(UniqueEmail(), "short", null, "fp", "pc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_too_short", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Login_SucceedsWithCorrectPassword()
    {
        var email = UniqueEmail();
        await RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", "fp-login", "pc"));

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(email, auth.Account.Email);
    }

    [Fact]
    public async Task Login_IsUniformForWrongPasswordAndUnknownEmail()
    {
        var email = UniqueEmail();
        await RegisterAsync(_client, email);

        var wrongPassword = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "wrong-password", "fp", "pc"));
        var unknownEmail = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(UniqueEmail(), "password123", "fp", "pc"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(
            (await wrongPassword.Content.ReadFromJsonAsync<ErrorResponse>())!.Error,
            (await unknownEmail.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Me_RequiresToken()
    {
        var anonymous = await _client.GetAsync("/api/v1/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var auth = await RegisterAsync(_client, UniqueEmail());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var authorized = await _client.SendAsync(request);

        authorized.EnsureSuccessStatusCode();
        var account = (await authorized.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal(auth.Account.Id, account.Id);
    }

    [Fact]
    public async Task Me_RejectsForgedToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "a.b.c");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Usage_IsReadableByTheOwner()
    {
        var auth = await RegisterAsync(_client, UniqueEmail());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var usage = (await response.Content.ReadFromJsonAsync<UsageDto>())!;
        Assert.Equal(10_000, usage.QuotaMonthlyChars);
        Assert.Equal(10_000, usage.RemainingMonthlyChars);
    }

    [Fact]
    public async Task Refresh_RotatesAndInvalidatesOldToken()
    {
        var auth = await RegisterAsync(_client, UniqueEmail());

        var first = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        first.EnsureSuccessStatusCode();
        var rotated = (await first.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(auth.RefreshToken, rotated.RefreshToken);

        var replay = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshChain()
    {
        var auth = await RegisterAsync(_client, UniqueEmail());

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        (await _client.SendAsync(logout)).EnsureSuccessStatusCode();

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task DeviceCap_IsEnforcedPerAccount()
    {
        var email = UniqueEmail();
        var auth = await RegisterAsync(_client, email);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/devices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Content = JsonContent.Create(new DeviceRequest("extra-" + i, "pc"));
            statuses.Add((await _client.SendAsync(request)).StatusCode);
        }

        // Register consumed one slot; only two additions remain before the cap of three.
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.OK, statuses[1]);
        Assert.Equal(HttpStatusCode.BadRequest, statuses[2]);
    }

    [Fact]
    public async Task Admin_RequiresTokenAndReportsMetrics()
    {
        await RegisterAsync(_client, UniqueEmail());

        var anonymous = await _client.GetAsync("/api/v1/admin/metrics");
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/metrics");
        request.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
        var authorized = await _client.SendAsync(request);

        authorized.EnsureSuccessStatusCode();
        var metrics = (await authorized.Content.ReadFromJsonAsync<AdminMetricsResponse>())!;
        Assert.True(metrics.TotalUsers >= 1);
    }

    [Fact]
    public async Task Admin_UpgradesPlanAndUsageReflectsNewQuota()
    {
        var email = UniqueEmail();
        await RegisterAsync(_client, email);

        using (var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan"))
        {
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            upgrade.Content = JsonContent.Create(new PlanChangeRequest("standard"));
            (await _client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", "fp-upgrade", "pc"));
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;

        Assert.Equal("standard", auth.Account.Plan);
        Assert.Equal(200_000, auth.Usage.QuotaMonthlyChars);
        Assert.Null(auth.Usage.QuotaDailyChars);
    }

    [Fact]
    public async Task RequestCode_IsDisabledInThisConfiguration()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/code",
            new VerificationCodeRequest(UniqueEmail()));

        response.EnsureSuccessStatusCode();
        Assert.Equal("verification_disabled", (await response.Content.ReadFromJsonAsync<MessageResponse>())!.Message);
    }

    [Fact]
    public async Task RequestCode_RejectsMalformedEmail()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/code", new VerificationCodeRequest("not-an-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
