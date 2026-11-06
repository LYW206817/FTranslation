using FastTranslation.Core.Notes;
using Xunit;

namespace FastTranslation.Core.Tests;

public class ReviewScheduleTests
{
    // 8 stage × 4 grade = 32 组，第一行是 stage 0（新词）
    [Theory]
    // stage 0：新词，从未排期
    [InlineData(0, ReviewGrade.Forgot, 1)]
    [InlineData(0, ReviewGrade.Fuzzy, 2)]
    [InlineData(0, ReviewGrade.Familiar, 3)]
    [InlineData(0, ReviewGrade.Known, 4)]
    // stage 1（10 分钟档）→ known 回到 4
    [InlineData(1, ReviewGrade.Forgot, 1)]
    [InlineData(1, ReviewGrade.Fuzzy, 2)]
    [InlineData(1, ReviewGrade.Familiar, 3)]
    [InlineData(1, ReviewGrade.Known, 4)]
    // stage 2（1 天档）
    [InlineData(2, ReviewGrade.Forgot, 1)]
    [InlineData(2, ReviewGrade.Fuzzy, 2)]
    [InlineData(2, ReviewGrade.Familiar, 3)]
    [InlineData(2, ReviewGrade.Known, 4)]
    // stage 3（3 天档）
    [InlineData(3, ReviewGrade.Forgot, 1)]
    [InlineData(3, ReviewGrade.Fuzzy, 2)]
    [InlineData(3, ReviewGrade.Familiar, 3)]
    [InlineData(3, ReviewGrade.Known, 4)]
    // stage 4（7 天档）→ known 升到 5
    [InlineData(4, ReviewGrade.Forgot, 1)]
    [InlineData(4, ReviewGrade.Fuzzy, 2)]
    [InlineData(4, ReviewGrade.Familiar, 3)]
    [InlineData(4, ReviewGrade.Known, 5)]
    // stage 5（14 天档）
    [InlineData(5, ReviewGrade.Forgot, 1)]
    [InlineData(5, ReviewGrade.Fuzzy, 2)]
    [InlineData(5, ReviewGrade.Familiar, 3)]
    [InlineData(5, ReviewGrade.Known, 6)]
    // stage 6（28 天档）
    [InlineData(6, ReviewGrade.Forgot, 1)]
    [InlineData(6, ReviewGrade.Fuzzy, 2)]
    [InlineData(6, ReviewGrade.Familiar, 3)]
    [InlineData(6, ReviewGrade.Known, 7)]
    // stage 7（56 天档，封顶）
    [InlineData(7, ReviewGrade.Forgot, 1)]
    [InlineData(7, ReviewGrade.Fuzzy, 2)]
    [InlineData(7, ReviewGrade.Familiar, 3)]
    [InlineData(7, ReviewGrade.Known, 7)]
    public void NextStage_MapsEveryCellOfTheLadder(int stage, ReviewGrade grade, int expected) =>
        Assert.Equal(expected, ReviewSchedule.NextStage(stage, grade));

    [Fact]
    public void Known_CapsAtMaxStage() =>
        Assert.Equal(ReviewSchedule.MaxStage, ReviewSchedule.NextStage(ReviewSchedule.MaxStage, ReviewGrade.Known));

    [Fact]
    public void Forgot_NeverReturnsToStageZero()
    {
        for (var stage = 0; stage <= ReviewSchedule.MaxStage; stage++)
        {
            Assert.True(ReviewSchedule.NextStage(stage, ReviewGrade.Forgot) >= 1);
        }
    }

    [Fact]
    public void Schedule_AddsTheIntervalOfTheNewStage()
    {
        const long now = 1_700_000_000L;
        var (stage, dueAt) = ReviewSchedule.Schedule(0, ReviewGrade.Known, now);
        Assert.Equal(4, stage);
        Assert.Equal(now + 604_800L, dueAt);
    }

    [Fact]
    public void Schedule_EachStageUsesItsOwnInterval()
    {
        const long now = 1_700_000_000L;
        var cases = new (int Current, ReviewGrade Grade, int Expected)[]
        {
            (0, ReviewGrade.Forgot, 1),
            (0, ReviewGrade.Fuzzy, 2),
            (0, ReviewGrade.Familiar, 3),
            (0, ReviewGrade.Known, 4),
            (4, ReviewGrade.Known, 5),
            (5, ReviewGrade.Known, 6),
            (6, ReviewGrade.Known, 7),
        };
        foreach (var (current, grade, expected) in cases)
        {
            var (stage, dueAt) = ReviewSchedule.Schedule(current, grade, now);
            Assert.Equal(expected, stage);
            Assert.Equal(now + ReviewSchedule.IntervalsSeconds[expected], dueAt);
        }
    }

    [Fact]
    public void Schedule_ZeroStageIsNeverDue()
    {
        // stage 0 的间隔是 0：新词不靠 due_at 进队，靠每日配额
        Assert.Equal(0L, ReviewSchedule.IntervalsSeconds[0]);
    }

    [Fact]
    public void Intervals_AreStrictlyIncreasing()
    {
        for (var stage = 1; stage <= ReviewSchedule.MaxStage; stage++)
        {
            Assert.True(ReviewSchedule.IntervalsSeconds[stage] > ReviewSchedule.IntervalsSeconds[stage - 1]);
        }
    }

    [Theory]
    [InlineData("forgot", ReviewGrade.Forgot)]
    [InlineData("FUZZY", ReviewGrade.Fuzzy)]
    [InlineData(" Familiar ", ReviewGrade.Familiar)]
    [InlineData("known", ReviewGrade.Known)]
    public void TryParseGrade_AcceptsWireNamesCaseInsensitively(string wire, ReviewGrade expected)
    {
        Assert.True(ReviewSchedule.TryParseGrade(wire, out var grade));
        Assert.Equal(expected, grade);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("easy")]
    public void TryParseGrade_RejectsUnknown(string? wire) =>
        Assert.False(ReviewSchedule.TryParseGrade(wire, out _));

    [Fact]
    public void ToWire_RoundTripsEveryGrade()
    {
        foreach (var grade in ReviewSchedule.AllGrades)
        {
            Assert.True(ReviewSchedule.TryParseGrade(ReviewSchedule.ToWire(grade), out var back));
            Assert.Equal(grade, back);
        }
    }

    [Fact]
    public void AllGrades_CoversEveryEnumValue()
    {
        Assert.Equal(Enum.GetValues<ReviewGrade>().Length, ReviewSchedule.AllGrades.Count);
        foreach (var grade in Enum.GetValues<ReviewGrade>())
        {
            Assert.Contains(grade, ReviewSchedule.AllGrades);
        }
    }

    [Fact]
    public void IntervalLabel_CoversEveryStage()
    {
        Assert.Equal(8, ReviewSchedule.IntervalsSeconds.Count);
        for (var stage = 0; stage <= ReviewSchedule.MaxStage; stage++)
        {
            Assert.False(string.IsNullOrWhiteSpace(ReviewSchedule.IntervalLabel(stage)));
        }
    }

    [Fact]
    public void Display_HasATextForEveryGrade()
    {
        foreach (var grade in ReviewSchedule.AllGrades)
        {
            Assert.False(string.IsNullOrWhiteSpace(ReviewSchedule.Display(grade)));
        }
    }

    /// <summary>v101: 日配额边界必须是调用方的本地零点,不是 UTC 零点。</summary>
    [Fact]
    public void StartOfLocalDay_UsesTheCallersOffset()
    {
        var now = new DateTimeOffset(2026, 11, 6, 7, 30, 0, TimeSpan.FromHours(8));

        var localMidnight = ReviewSchedule.StartOfLocalDay(now);
        var expected = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        Assert.Equal(expected, localMidnight);

        // 同一时刻的 UTC 零点是前一天 08:00（本地），两者相差 16 小时。
        var utcMidnight = now.ToUnixTimeSeconds() - now.ToUnixTimeSeconds() % 86_400L;
        Assert.Equal(16 * 3600L, localMidnight - utcMidnight);
    }

    [Fact]
    public void StartOfLocalDay_HandlesNegativeOffsets()
    {
        var now = new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.FromHours(-5));

        var expected = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.FromHours(-5)).ToUnixTimeSeconds();
        Assert.Equal(expected, ReviewSchedule.StartOfLocalDay(now));
        Assert.NotEqual(now.ToUnixTimeSeconds() - now.ToUnixTimeSeconds() % 86_400L,
            ReviewSchedule.StartOfLocalDay(now));
    }
}
