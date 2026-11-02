using FastTranslation.Server.Domain;
using FastTranslation.Server.Gateway;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>语音额度折算与会话并发闸门的纯逻辑测试(v81)。</summary>
public sealed class SpeechQuotaTests
{
    [Theory]
    [InlineData(1_000, 20, 10, 1_800, 50)]     // 剩余 1,000 字符 = 50 秒
    [InlineData(1_000_000, 20, 10, 1_800, 1_800)] // 额度充足时被单会话硬顶截断
    [InlineData(0, 20, 10, 1_800, 10)]         // 极端情况也保底 MinSeconds(调用方已预检)
    public void SessionSeconds_never_exceeds_quota_or_the_hard_cap(
        int remainingChars, int charsPerSecond, int minSeconds, int maxSeconds, int expected)
    {
        Assert.Equal(expected, SpeechQuota.SessionSeconds(remainingChars, charsPerSecond, minSeconds, maxSeconds));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(-1, 20)]
    public void SessionSeconds_rejects_invalid_arguments(int remainingChars, int charsPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpeechQuota.SessionSeconds(remainingChars, charsPerSecond, 10, 1_800));
    }

    [Fact]
    public void Session_limiter_rejects_a_second_session_and_frees_the_slot_on_exit()
    {
        var limiter = new SpeechSessionLimiter();
        var user = Guid.NewGuid();

        Assert.True(limiter.TryEnter(user, 1));
        Assert.False(limiter.TryEnter(user, 1));
        limiter.Exit(user);
        Assert.True(limiter.TryEnter(user, 1));
    }

    [Fact]
    public void Session_limiter_with_zero_limit_allows_every_session()
    {
        var limiter = new SpeechSessionLimiter();
        var user = Guid.NewGuid();

        Assert.True(limiter.TryEnter(user, 0));
        Assert.True(limiter.TryEnter(user, 0));
        Assert.True(limiter.TryEnter(user, 0));
    }

    [Fact]
    public void Session_limiter_exit_without_enter_is_harmless()
    {
        new SpeechSessionLimiter().Exit(Guid.NewGuid());
    }
}
