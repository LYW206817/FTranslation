using FastTranslation.Core.Account;
using Xunit;

namespace FastTranslation.Core.Tests;

/// <summary>
/// 与服务端 CredentialRulesTests 用同一组用例钉住同一规则:
/// 服务端不引用 Core,代码无法共享,只能两边各写一份、用例保持一致。
/// </summary>
public class EmailFormatTests
{
    [Theory]
    [InlineData("a@b.com")]
    [InlineData("  user.name+tag@example.co.uk  ")]
    [InlineData("x@y.io")]
    [InlineData("user_name@example.com")]
    [InlineData("user-name@example.com")]
    [InlineData("a@sub.example.com")]
    [InlineData("o'brien@example.com")]
    [InlineData("a!#$%&'*+/=?^_`{|}~-@example.com")]
    public void Accepts_realistic_addresses(string email)
        => Assert.True(EmailFormat.IsValid(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@nodomain.com")]
    [InlineData("nolocal@")]
    [InlineData("two@at@x.com")]
    [InlineData("nodot@localhost")]
    [InlineData("dot@.com")]
    [InlineData("dot@com.")]
    [InlineData("a b@example.com")]
    [InlineData("a\tb@example.com")]
    [InlineData(".a@example.com")]
    [InlineData("a.@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a@example..com")]
    [InlineData("a@-example.com")]
    [InlineData("a@example-.com")]
    [InlineData("a@b.c")]
    [InlineData("a@b.123")]
    [InlineData("a(b)@example.com")]
    [InlineData("a@exam_ple.com")]
    [InlineData("张三@example.com")]
    [InlineData("a@例子.公司")]
    public void Rejects_malformed_addresses(string? email)
        => Assert.False(EmailFormat.IsValid(email));

    [Fact]
    public void Rejects_overlong_address()
    {
        var local = new string('a', 250);
        Assert.False(EmailFormat.IsValid(local + "@b.com"));
    }

    [Fact]
    public void Rejects_overlong_local_part()
    {
        var local = new string('a', 65);
        Assert.False(EmailFormat.IsValid(local + "@example.com"));
    }

    [Fact]
    public void Rejects_overlong_domain_label()
    {
        var label = new string('a', 64);
        Assert.False(EmailFormat.IsValid("a@" + label + ".com"));
    }

    [Fact]
    public void Accepts_local_part_at_the_limit()
    {
        var local = new string('a', 64);
        Assert.True(EmailFormat.IsValid(local + "@example.com"));
    }
}