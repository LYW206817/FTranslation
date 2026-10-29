using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// The account page's two write paths: the display name and the password. Both are the only
/// user-editable things on the server, so the tests here are about the guards (length, control
/// characters, the current password) rather than about happy-path plumbing.
/// </summary>
public class AccountProfileTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;
    private readonly HttpClient _client;

    public AccountProfileTests(ServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string UniqueEmail() => $"profile-{Guid.NewGuid():N}@example.com";

    private async Task<(HttpClient Client, AuthResponse Auth)> SignedInAsync(string password = "password123")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(UniqueEmail(), password, null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    [Fact]
    public async Task Profile_StartsEmptyAndRenamesTheAccount()
    {
        var (client, auth) = await SignedInAsync();
        Assert.Equal(string.Empty, auth.Account.DisplayName);

        var response = await client.PatchAsJsonAsync("/api/v1/account/profile",
            new UpdateProfileRequest("  永威  "));

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<AccountDto>())!;
        // the value is trimmed before it is stored: a name is rendered in a single-line label
        Assert.Equal("永威", updated.DisplayName);
        Assert.Equal(auth.Account.Email, updated.Email);
    }

    [Fact]
    public async Task Profile_BlankClearsTheNameBackToTheEmail()
    {
        var (client, _) = await SignedInAsync();
        (await client.PatchAsJsonAsync("/api/v1/account/profile", new UpdateProfileRequest("永威")))
            .EnsureSuccessStatusCode();

        var response = await client.PatchAsJsonAsync("/api/v1/account/profile",
            new UpdateProfileRequest("   "));

        response.EnsureSuccessStatusCode();
        Assert.Equal(string.Empty, (await response.Content.ReadFromJsonAsync<AccountDto>())!.DisplayName);
    }

    [Fact]
    public async Task Profile_RejectsLongAndMultiLineNames()
    {
        var (client, _) = await SignedInAsync();

        var tooLong = await client.PatchAsJsonAsync("/api/v1/account/profile",
            new UpdateProfileRequest(new string('x', 25)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("display_name_too_long",
            (await tooLong.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var multiLine = await client.PatchAsJsonAsync("/api/v1/account/profile",
            new UpdateProfileRequest("第一行\n第二行"));
        Assert.Equal(HttpStatusCode.BadRequest, multiLine.StatusCode);
        Assert.Equal("display_name_invalid",
            (await multiLine.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Profile_NeedsAToken()
    {
        var response = await _client.PatchAsJsonAsync("/api/v1/account/profile",
            new UpdateProfileRequest("nobody"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Password_WrongCurrentPasswordChangesNothing()
    {
        var (client, _) = await SignedInAsync("password123");

        var response = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("not-my-password", "brand-new-123", "fp-pw", "pc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_mismatch", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Password_TooShortOrRepeatedIsRefused()
    {
        var (client, _) = await SignedInAsync("password123");

        var shortOne = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("password123", "short", "fp-pw", "pc"));
        Assert.Equal(HttpStatusCode.BadRequest, shortOne.StatusCode);
        Assert.Equal("password_too_short", (await shortOne.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var same = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("password123", "password123", "fp-pw", "pc"));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal("password_unchanged", (await same.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Password_ChangeKeepsThisDeviceInAndRetiresTheOldPassword()
    {
        var email = UniqueEmail();
        var password = "password123";
        var client = _factory.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        registered.EnsureSuccessStatusCode();
        var auth = (await registered.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var changed = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest(password, "brand-new-123", "fp-pw", "pc"));
        changed.EnsureSuccessStatusCode();
        var fresh = (await changed.Content.ReadFromJsonAsync<AuthResponse>())!;

        // the caller gets a usable pair back instead of being logged out of its own change
        Assert.False(string.IsNullOrWhiteSpace(fresh.AccessToken));
        Assert.NotEqual(auth.RefreshToken, fresh.RefreshToken);

        var newLogin = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "brand-new-123", "fp-new", "pc"));
        newLogin.EnsureSuccessStatusCode();

        var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, password, "fp-old", "pc"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
    }

    [Fact]
    public async Task Password_ChangeRevokesOtherDevices()
    {
        var email = UniqueEmail();
        var client = _factory.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-first", "pc"));
        registered.EnsureSuccessStatusCode();
        var auth = (await registered.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var other = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", "fp-other", "laptop"));
        other.EnsureSuccessStatusCode();
        var otherAuth = (await other.Content.ReadFromJsonAsync<AuthResponse>())!;

        (await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("password123", "brand-new-123", "fp-first", "pc")))
            .EnsureSuccessStatusCode();

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(otherAuth.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }
}
