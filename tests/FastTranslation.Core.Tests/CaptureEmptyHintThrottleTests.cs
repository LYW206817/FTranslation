using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class CaptureEmptyHintThrottleTests
{
    [Fact]
    public void First_empty_capture_is_reported()
    {
        var throttle = new CaptureEmptyHintThrottle(() => DateTimeOffset.UnixEpoch);

        Assert.True(throttle.ShouldShow());
    }

    [Fact]
    public void Second_empty_capture_within_24_hours_is_suppressed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new CaptureEmptyHintThrottle(() => now);

        Assert.True(throttle.ShouldShow());
        now = now.AddHours(23);
        Assert.False(throttle.ShouldShow());
    }

    [Fact]
    public void Hint_returns_after_24_hours()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new CaptureEmptyHintThrottle(() => now);

        Assert.True(throttle.ShouldShow());
        now = now.AddHours(24);
        Assert.True(throttle.ShouldShow());
    }
}