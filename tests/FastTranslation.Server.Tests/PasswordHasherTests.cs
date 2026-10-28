using FastTranslation.Server.Auth;
using Xunit;

namespace FastTranslation.Server.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verify_AcceptsTheOriginalPassword()
    {
        var stored = PasswordHasher.Hash("correct horse battery", iterations: 1_000);

        Assert.True(PasswordHasher.Verify("correct horse battery", stored));
    }

    [Fact]
    public void Verify_RejectsDifferentPassword()
    {
        var stored = PasswordHasher.Hash("correct horse battery", iterations: 1_000);

        Assert.False(PasswordHasher.Verify("correct horse batterY", stored));
        Assert.False(PasswordHasher.Verify(string.Empty, stored));
        Assert.False(PasswordHasher.Verify("correct horse battery ", stored));
    }

    [Fact]
    public void Hash_ProducesDistinctSalts()
    {
        var a = PasswordHasher.Hash("same-password", iterations: 1_000);
        var b = PasswordHasher.Hash("same-password", iterations: 1_000);

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same-password", a));
        Assert.True(PasswordHasher.Verify("same-password", b));
    }

    [Fact]
    public void Hash_RecordsSchemeAndIterations()
    {
        var stored = PasswordHasher.Hash("pw", iterations: 1_234);

        var parts = stored.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("1234", parts[1]);
    }

    [Fact]
    public void DefaultIterations_IsTunedForStorageNotTests()
        => Assert.True(PasswordHasher.DefaultIterations >= 100_000);

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("pbkdf2-sha256$1000$not-base64$also-not")]
    [InlineData("bcrypt$10$abc$def")]
    [InlineData("pbkdf2-sha256$x$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$$")]
    public void Verify_RejectsMalformedStoredValues(string stored)
        => Assert.False(PasswordHasher.Verify("pw", stored));

    [Fact]
    public void Verify_RejectsNullStoredValue()
        => Assert.False(PasswordHasher.Verify("pw", null!));
}
