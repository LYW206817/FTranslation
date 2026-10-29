using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>兑换码是手工输入的,所以规范化必须宽容,同时绝不能撞码。</summary>
public class RedeemCodeTests
{
    [Fact]
    public void Generate_ProducesThePrintedShape()
    {
        var code = RedeemCode.Generate();

        Assert.StartsWith("FT-", code, StringComparison.Ordinal);
        Assert.Equal(2 + (RedeemCode.RawLength / 4 * 5), code.Length);
        Assert.All(code.Replace("-", string.Empty, StringComparison.Ordinal)[2..], character =>
            Assert.Contains(character, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public void Generate_DoesNotRepeat()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => RedeemCode.Generate()).ToHashSet();

        Assert.Equal(200, codes.Count);
    }

    [Theory]
    [InlineData("FT-4G7M-2XQP-9HKV-3WTA")]
    [InlineData("ft-4g7m-2xqp-9hkv-3wta")]
    [InlineData("4G7M2XQP9HKV3WTA")]
    [InlineData("FT4G7M2XQP9HKV3WTA")]
    [InlineData("  FT-4G7M-2XQP-9HKV-3WTA  ")]
    public void Normalize_AcceptsThePrintedFormAndWhatPeopleRetype(string input)
    {
        Assert.Equal("4G7M2XQP9HKV3WTA", RedeemCode.Normalize(input));
    }

    [Theory]
    [InlineData("FT-4G7M-2XQP-9HKV-3WTA", "4G7M2XQP9HKV3WTA")]
    [InlineData("FT-OG7M-2XQP-9HKV-3WTA", "0G7M2XQP9HKV3WTA")]
    [InlineData("FT-IG7M-2XQP-9HKV-3WTA", "1G7M2XQP9HKV3WTA")]
    [InlineData("FT-LG7M-2XQP-9HKV-3WTA", "1G7M2XQP9HKV3WTA")]
    [InlineData("FT-UG7M-2XQP-9HKV-3WTA", "VG7M2XQP9HKV3WTA")]
    public void Normalize_FoldsTheLettersTheAlphabetDoesNotUse(string input, string expected)
    {
        Assert.Equal(expected, RedeemCode.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("FT-4G7M-2XQP-9HKV-3WT")]     // 少一个字符
    [InlineData("FT-4G7M-2XQP-9HKV-3WTAZ")]   // 多一个字符
    public void Normalize_RejectsAnythingThatIsNotOneCode(string? input)
    {
        Assert.Equal(string.Empty, RedeemCode.Normalize(input));
    }
}
