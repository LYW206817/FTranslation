using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>把验证码截下来而不是真的发信：重置流程的断言需要拿到那六位数。</summary>
public sealed class CapturingMailSender : IVerificationMailSender
{
    public List<(string Email, string Code)> Sent { get; } = [];

    public Task SendCodeAsync(string email, string code, CancellationToken ct)
    {
        Sent.Add((email, code));
        return Task.CompletedTask;
    }
}

public class AuthLifecycleTests
{
    private static WebApplicationFactory<Program> FactoryWithMail(CapturingMailSender mail) =>
        new ServerFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVerificationMailSender>();
                services.AddSingleton<IVerificationMailSender>(mail);
            }));

    private static string UniqueEmail() => $"life-{Guid.NewGuid():N}@example.com";

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password, string fingerprint)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, password, fingerprint, "pc"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Reset_flow_changes_password_and_revokes_every_refresh_token()
    {
        var mail = new CapturingMailSender();
        using var factory = FactoryWithMail(mail);
        var client = factory.CreateClient();
        var email = UniqueEmail();
        const string oldPassword = "old-password-1";
        const string newPassword = "new-password-2";
        var first = await RegisterAsync(client, email, oldPassword);
        var second = await LoginAsync(client, email, oldPassword, "fp-second");

        var codeResponse = await client.PostAsJsonAsync("/api/v1/auth/password/reset/code",
            new PasswordResetCodeRequest(email));
        codeResponse.EnsureSuccessStatusCode();
        var sent = Assert.Single(mail.Sent);
        var wrongCode = sent.Code == "000000" ? "000001" : "000000";

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/password/reset",
            new PasswordResetRequest(email, wrongCode, newPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var reset = await client.PostAsJsonAsync("/api/v1/auth/password/reset",
            new PasswordResetRequest(email, sent.Code, newPassword));
        reset.EnsureSuccessStatusCode();

        // 旧密码不能再用，新密码可以登录。
        var oldLogin = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, oldPassword, "fp-old", "pc"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await LoginAsync(client, email, newPassword, "fp-new");

        // 重置前签发的两个 refresh token 都必须失效；access token 是无状态的，最多再活 30 分钟。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken))).StatusCode);

        // 验证码用后即焚
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/password/reset",
            new PasswordResetRequest(email, sent.Code, newPassword));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task Reset_code_does_not_reveal_whether_the_email_exists()
    {
        var mail = new CapturingMailSender();
        using var factory = FactoryWithMail(mail);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password/reset/code",
            new PasswordResetCodeRequest(UniqueEmail()));

        response.EnsureSuccessStatusCode();
        Assert.Equal("code_sent", (await response.Content.ReadFromJsonAsync<MessageResponse>())!.Message);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Delete_requires_password_and_email_confirmation()
    {
        var mail = new CapturingMailSender();
        using var factory = FactoryWithMail(mail);
        var client = factory.CreateClient();
        var email = UniqueEmail();
        const string password = "password-123";
        var auth = await RegisterAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/account/delete",
            new DeleteAccountRequest("wrong", email));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Equal("password_mismatch", (await wrongPassword.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var wrongConfirm = await client.PostAsJsonAsync("/api/v1/account/delete",
            new DeleteAccountRequest(password, "someone@example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongConfirm.StatusCode);
        Assert.Equal("confirmation_mismatch", (await wrongConfirm.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var deleted = await client.PostAsJsonAsync("/api/v1/account/delete",
            new DeleteAccountRequest(password, email));
        deleted.EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Authorization = null;
        // 账号已不存在：登录 401，旧 refresh token 失效，同一个邮箱可以重新注册。
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, password, "fp-after-delete", "pc"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);

        var reRegister = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, null, "fp-reregister", "pc"));
        reRegister.EnsureSuccessStatusCode();
    }
}
