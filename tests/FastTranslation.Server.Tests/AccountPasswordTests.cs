using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>认证限流压到 2 次/分的工厂，用来证明改密接口确实挂了限流。</summary>
public class LowAuthLimitFactory : ServerFactory
{
    protected override int AuthRequestsPerMinute => 2;
}

public class AccountPasswordTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public AccountPasswordTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ChangePassword_RejectsOverlongNewPassword()
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"pwd-{Guid.NewGuid():N}@example.com", "password123", null,
                "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        // 修前只查长度下限，129 字符的新密码会被接受。
        var response = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("password123", new string('a', 129), "fp-change", "pc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_too_long",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }
}

public class PasswordRateLimitTests : IClassFixture<LowAuthLimitFactory>
{
    private readonly LowAuthLimitFactory _factory;

    public PasswordRateLimitTests(LowAuthLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ChangePassword_IsRateLimited()
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"pwd-limit-{Guid.NewGuid():N}@example.com", "password123", null,
                "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        // 注册已用掉 1 次；第一次改密用掉第 2 次（密码不匹配，400），下一次必须被限流。
        var first = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("wrong-password", "password123", "fp-change", "pc"));
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/account/password",
            new ChangePasswordRequest("wrong-password", "password123", "fp-change", "pc"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }
}
