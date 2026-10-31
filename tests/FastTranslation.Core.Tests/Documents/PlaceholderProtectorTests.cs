using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public sealed class PlaceholderProtectorTests
{
    [Theory]
    [InlineData("请访问 https://example.com/a?b=1 查看详情")]
    [InlineData("回调里写 `client.Send()` 就行")]
    [InlineData("```\nvar x = 1;\n```\n\n这一整块是代码")]
    [InlineData("发到 someone@example.com 即可")]
    [InlineData("模板是 {0} 和 %s 两种占位")]
    [InlineData("把 <b>加粗</b> 换成 <em>斜体</em>")]
    public void Protect_restore_roundtrip_keeps_all_placeholders(string source)
    {
        var protectedText = PlaceholderProtector.Protect(source, out var originals);
        Assert.NotEqual(source, protectedText);
        Assert.NotEmpty(originals);

        // 模拟一面只翻译自然语言、原样保留哨兵的模型：把哨兵摘出来再原样放回。
        var restored = PlaceholderProtector.Restore(protectedText, originals, out var ok);

        Assert.True(ok);
        Assert.Equal(source, restored);
    }

    [Fact]
    public void Protect_leaves_plain_sentences_alone()
    {
        var protectedText = PlaceholderProtector.Protect("这是一句普通的话", out var originals);

        Assert.Equal("这是一句普通的话", protectedText);
        Assert.Empty(originals);
    }

    [Fact]
    public void Protect_numbers_sentinels_in_reading_order()
    {
        var protectedText = PlaceholderProtector.Protect("先 `a` 再 https://x.dev 最后 {0}", out var originals);

        Assert.Equal(3, originals.Count);
        Assert.Equal("`a`", originals[0]);
        Assert.Equal("https://x.dev", originals[1]);
        Assert.Equal("{0}", originals[2]);
        Assert.Contains("⟦1⟧", protectedText, StringComparison.Ordinal);
        Assert.Contains("⟦3⟧", protectedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_fails_when_one_placeholder_is_missing()
    {
        var protectedText = PlaceholderProtector.Protect("先 `a` 再 `b`", out var originals);
        var broken = protectedText.Replace("⟦2⟧", string.Empty, StringComparison.Ordinal);

        var restored = PlaceholderProtector.Restore(broken, originals, out var ok);

        Assert.False(ok);
        Assert.Equal(broken, restored);
    }

    [Fact]
    public void Restore_fails_when_placeholder_duplicated()
    {
        var protectedText = PlaceholderProtector.Protect("先 `a` 再 `b`", out var originals);
        var broken = protectedText.Replace("⟦2⟧", "⟦1⟧", StringComparison.Ordinal);

        _ = PlaceholderProtector.Restore(broken, originals, out var ok);

        Assert.False(ok);
    }

    [Fact]
    public void Restore_fails_on_unknown_sentinel_number()
    {
        _ = PlaceholderProtector.Restore("只有 ⟦9⟧ 一个哨兵", ["`a`"], out var ok);

        Assert.False(ok);
    }

    [Fact]
    public void Restore_accepts_text_without_placeholders_when_there_were_none()
    {
        var restored = PlaceholderProtector.Restore("纯译文", [], out var ok);

        Assert.True(ok);
        Assert.Equal("纯译文", restored);
    }
}
