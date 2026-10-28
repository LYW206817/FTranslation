using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class CardStatusLineTests
{
    private static TranslationResult Result(long elapsed = 531) =>
        new("hello", "你好", "auto", "Chinese", "qwen-mt-flash", elapsed, FromCache: false);

    [Fact]
    public void Without_usage_or_notice_it_is_the_old_single_line()
    {
        Assert.Equal("qwen-mt-flash · 531ms", ResultCardWindow.StatusLine(Result()));
    }

    [Fact]
    public void Cache_hit_still_marks_itself()
    {
        var cached = Result() with { FromCache = true };

        Assert.Equal("qwen-mt-flash · 531ms · 缓存", ResultCardWindow.StatusLine(cached));
    }

    [Fact]
    public void Usage_is_appended_as_a_total()
    {
        var withUsage = Result() with { Usage = new TokenUsage(56, 9) };

        Assert.Equal("qwen-mt-flash · 531ms · 65 tokens", ResultCardWindow.StatusLine(withUsage));
    }

    [Fact]
    public void Notice_explains_why_the_users_own_key_was_used()
    {
        var fallback = Result() with
        {
            Usage = new TokenUsage(56, 9),
            Notice = "本月额度已用完,这次用了你自己的 Key",
        };

        Assert.Equal(
            "qwen-mt-flash · 531ms · 65 tokens · 本月额度已用完,这次用了你自己的 Key",
            ResultCardWindow.StatusLine(fallback));
    }
}
