using FastTranslation.Server.Auth;
using Xunit;

namespace FastTranslation.Server.Tests;

public class CredentialRulesTests
{
    [Theory]
    [InlineData("a@b.com")]
    [InlineData("  user.name+tag@example.co.uk  ")]
    [InlineData("x@y.io")]
    public void ValidateEmail_AcceptsRealisticAddresses(string email)
        => Assert.Null(CredentialRules.ValidateEmail(email));

    [Theory]
    [InlineData(null, "email_required")]
    [InlineData("", "email_required")]
    [InlineData("   ", "email_required")]
    [InlineData("no-at-sign", "email_invalid")]
    [InlineData("@nodomain.com", "email_invalid")]
    [InlineData("nolocal@", "email_invalid")]
    [InlineData("two@at@x.com", "email_invalid")]
    [InlineData("nodot@localhost", "email_invalid")]
    [InlineData("dot@.com", "email_invalid")]
    [InlineData("dot@com.", "email_invalid")]
    public void ValidateEmail_RejectsMalformedAddresses(string? email, string expected)
        => Assert.Equal(expected, CredentialRules.ValidateEmail(email));

    [Fact]
    public void ValidateEmail_RejectsOverlongAddress()
    {
        var local = new string('a', 250);
        Assert.Equal("email_too_long", CredentialRules.ValidateEmail(local + "@b.com"));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("a-longer-password-with-symbols!@#")]
    public void ValidatePassword_AcceptsValidLength(string password)
        => Assert.Null(CredentialRules.ValidatePassword(password));

    [Theory]
    [InlineData(null, "password_required")]
    [InlineData("", "password_required")]
    [InlineData("short", "password_too_short")]
    [InlineData("1234567", "password_too_short")]
    public void ValidatePassword_RejectsShortPasswords(string? password, string expected)
        => Assert.Equal(expected, CredentialRules.ValidatePassword(password));

    [Fact]
    public void ValidatePassword_RejectsOverlongPassword()
        => Assert.Equal("password_too_long", CredentialRules.ValidatePassword(new string('a', 129)));
}
