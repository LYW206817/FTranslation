using System.Diagnostics;
using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public sealed class RequestPacerTests
{
    [Fact]
    public async Task First_request_does_not_wait()
    {
        var pacer = new RequestPacer(600, 60_000_000);
        var watch = Stopwatch.StartNew();

        await pacer.WaitAsync(100, CancellationToken.None);

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 150, $"first request waited {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Later_requests_are_spread_by_the_interval()
    {
        // 600 RPM = 每 100ms 一个，三次共约 200ms；给 100ms 余量吸收调度抖动。
        var pacer = new RequestPacer(600, 60_000_000);
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < 3; i++) await pacer.WaitAsync(100, CancellationToken.None);

        watch.Stop();
        Assert.InRange(watch.ElapsedMilliseconds, 150, 900);
    }

    [Fact]
    public async Task Wait_is_cancellable()
    {
        // 1 RPM = 要等 60 秒；取消必须立刻返回，不能等满。
        var pacer = new RequestPacer(1, 60_000_000);
        await pacer.WaitAsync(100, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.WaitAsync(100, cts.Token));
    }

    [Fact]
    public async Task Large_requests_are_spaced_by_their_char_count()
    {
        // 每分钟 6,000 字符：一笔 3,000 字符占 30 秒，下一笔必须等满 30 秒。
        // 请求数给足够大，确保拦住它的是字符量而不是 RPM。
        var pacer = new RequestPacer(60_000, 6_000);
        await pacer.WaitAsync(3_000, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.WaitAsync(3_000, cts.Token));

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds >= 400, $"second request returned after {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Small_requests_are_limited_by_the_request_rate_not_the_char_rate()
    {
        // 每分钟 600 万字符（一笔 100 字符只占 1ms）、6,000 RPM（10ms 间隔）：
        // 拦住它的是请求数而不是字符量，所以 10 笔约 90ms 走完。
        var pacer = new RequestPacer(6_000, 6_000_000);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) await pacer.WaitAsync(100, CancellationToken.None);

        watch.Stop();
        Assert.InRange(watch.ElapsedMilliseconds, 60, 400);
    }

    [Fact]
    public async Task Failures_back_off_exponentially_and_skip_the_last_wait()
    {
        // 1s、2s、4s……；最后一次失败之后没必要再等（等完就放弃了）。
        var pacer = new RequestPacer(60_000, 60_000_000, TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();
        await pacer.WaitAfterFailureAsync(0, 5, CancellationToken.None);
        await pacer.WaitAfterFailureAsync(1, 5, CancellationToken.None);
        var twoCalls = watch.ElapsedMilliseconds;
        Assert.InRange(twoCalls, 250, 900);

        watch.Restart();
        await pacer.WaitAfterFailureAsync(4, 5, CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds < 60, $"last attempt waited {watch.ElapsedMilliseconds}ms");
    }
}
