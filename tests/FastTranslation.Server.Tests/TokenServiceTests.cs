using FastTranslation.Server.Auth;
using Xunit;

namespace FastTranslation.Server.Tests;

public class TokenServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static TokenService Create(TimeSpan? lifetime = null)
        => new(Key, lifetime ?? TimeSpan.FromMinutes(30));

    [Fact]
    public void CreateAccessToken_RoundTripsClaims()
    {
        var service = Create();
        var userId = Guid.NewGuid();

        var token = service.CreateAccessToken(userId, "a@b.com", "light", out var expiresAt);

        Assert.True(service.TryValidateAccessToken(token, out var principal));
        Assert.Equal(userId, principal.UserId);
        Assert.Equal("a@b.com", principal.Email);
        Assert.Equal("light", principal.Plan);
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_RejectsTamperedPayload()
    {
        var service = Create();
        var token = service.CreateAccessToken(Guid.NewGuid(), "a@b.com", "free", out _);

        var parts = token.Split('.');
        var forged = parts[0] + "." + parts[1][..^2] + "AA." + parts[2];

        Assert.False(service.TryValidateAccessToken(forged, out _));
    }

    [Fact]
    public void Validate_RejectsTokenSignedByAnotherKey()
    {
        var otherKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var token = new TokenService(otherKey, TimeSpan.FromMinutes(30))
            .CreateAccessToken(Guid.NewGuid(), "a@b.com", "free", out _);

        Assert.False(Create().TryValidateAccessToken(token, out _));
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var service = new TokenService(Key, TimeSpan.FromMinutes(30), time);
        var token = service.CreateAccessToken(Guid.NewGuid(), "a@b.com", "free", out _);

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(service.TryValidateAccessToken(token, out _));
    }

    [Fact]
    public void Validate_RejectsAlgNone()
    {
        var service = Create();
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"iss\":\"fasttranslation-server\",\"aud\":\"fasttranslation-client\",\"sub\":\"00000000-0000-0000-0000-000000000001\",\"exp\":9999999999}");

        Assert.False(service.TryValidateAccessToken(header + "." + payload + ".", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData(null)]
    public void Validate_RejectsMalformedInput(string? token)
        => Assert.False(Create().TryValidateAccessToken(token, out _));

    [Fact]
    public void Constructor_RejectsShortKey()
        => Assert.Throws<ArgumentException>(() => new TokenService(new byte[16], TimeSpan.FromMinutes(1)));

    [Fact]
    public void RefreshToken_IsRandomAndHashedConsistently()
    {
        var a = TokenService.CreateRefreshToken();
        var b = TokenService.CreateRefreshToken();

        Assert.NotEqual(a, b);
        Assert.Equal(TokenService.HashRefreshToken(a), TokenService.HashRefreshToken(a));
        Assert.NotEqual(a, TokenService.HashRefreshToken(a));
        Assert.DoesNotContain("+", a, StringComparison.Ordinal);
        Assert.DoesNotContain("/", a, StringComparison.Ordinal);
        Assert.DoesNotContain("=", a, StringComparison.Ordinal);
    }

    private static string Base64Url(string text)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
