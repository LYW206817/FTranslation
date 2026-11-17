using FastTranslation.Server.Gateway;
using Xunit;

namespace FastTranslation.Server.Tests;

public class GatewayRateLimiterTests
{
    [Fact]
    public void TryAcquire_allows_the_limit_and_rejects_the_next_call()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var limiter = new GatewayRateLimiter(clock);
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user, 3));
        Assert.True(limiter.TryAcquire(user, 3));
        Assert.True(limiter.TryAcquire(user, 3));
        Assert.False(limiter.TryAcquire(user, 3));
    }

    [Fact]
    public void TryAcquire_starts_a_new_window_at_the_next_minute()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var limiter = new GatewayRateLimiter(clock);
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user, 2));
        Assert.True(limiter.TryAcquire(user, 2));
        Assert.False(limiter.TryAcquire(user, 2));

        // 30 秒后整分滚动：旧窗口的计数不能带进新窗口。
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(user, 2));
    }

    [Fact]
    public void TryAcquire_keeps_users_independent()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var limiter = new GatewayRateLimiter(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(first, 1));
        Assert.False(limiter.TryAcquire(first, 1));
        Assert.True(limiter.TryAcquire(second, 1));
    }

    [Fact]
    public void TryAcquire_treats_a_non_positive_limit_as_unlimited()
    {
        var limiter = new GatewayRateLimiter(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user, 0));
        Assert.True(limiter.TryAcquire(user, -1));
    }

    [Fact]
    public void Prune_removes_only_windows_that_have_rolled_over()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var limiter = new GatewayRateLimiter(clock);
        var stale = Guid.NewGuid();
        var current = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(stale, 1));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(current, 2));

        // 此时 stale 的窗口已经滚过（ResetAt <= now），current 的还在。
        Assert.Equal(1, limiter.Prune());
        Assert.Equal(0, limiter.Prune());
        Assert.True(limiter.TryAcquire(current, 2));
    }

    /// <summary>限流窗口按 UTC 秒计算，测试只推进时钟、不等待真实时间。</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
