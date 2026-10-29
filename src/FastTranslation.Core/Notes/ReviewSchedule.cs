namespace FastTranslation.Core.Notes;

/// <summary>
/// How well the user recalled a card. Order matters: the higher the value, the further out the card
/// is scheduled. The wire names are the API contract and must not change.
/// </summary>
public enum ReviewGrade
{
    Forgot = 0,
    Fuzzy = 1,
    Familiar = 2,
    Known = 3,
}

/// <summary>
/// A fixed ladder, not SM-2. The product asks for "classify it and bring it back later", which two
/// counters and one lookup table answer exactly. Pure functions only — no clock, no IO — so the
/// transition table is testable without a fixture.
/// </summary>
public static class ReviewSchedule
{
    public const int MaxStage = 7;

    /// <summary>Seconds to wait after grading, indexed by the resulting stage. Index 0 means "never graded".</summary>
    public static readonly IReadOnlyList<long> IntervalsSeconds = new long[]
    {
        0L, 600L, 86_400L, 259_200L, 604_800L, 1_209_600L, 2_419_200L, 4_838_400L,
    };

    /// <summary>Display order of the four buttons, left to right.</summary>
    public static readonly IReadOnlyList<ReviewGrade> AllGrades = new[]
    {
        ReviewGrade.Forgot, ReviewGrade.Fuzzy, ReviewGrade.Familiar, ReviewGrade.Known,
    };

    private static readonly string[] Labels =
    [
        "未排期", "10 分钟后", "1 天后", "3 天后", "7 天后", "14 天后", "28 天后", "56 天后",
    ];

    private static readonly string[] GradeLabels = ["忘记", "模糊", "熟悉", "记住"];

    /// <summary>
    /// Four grades map onto four rungs. 记住 jumps to 7 days on the first try (the user already knows
    /// it, there is no reason to show it again today) and then walks up one rung per success up to 56
    /// days. Every downgrade has a floor of stage 1: stage 0 stays reserved for "never graded", so a
    /// forgotten card is never demoted back into the new-card pile.
    /// </summary>
    public static int NextStage(int stage, ReviewGrade grade)
    {
        var current = Math.Clamp(stage, 0, MaxStage);
        return grade switch
        {
            ReviewGrade.Known => current >= 4 ? Math.Min(MaxStage, current + 1) : 4,
            ReviewGrade.Familiar => 3,
            ReviewGrade.Fuzzy => 2,
            _ => 1,
        };
    }

    /// <summary>Resulting stage plus the absolute due time in unix seconds.</summary>
    public static (int Stage, long DueAt) Schedule(int currentStage, ReviewGrade grade, long nowUnix)
    {
        var stage = NextStage(currentStage, grade);
        return (stage, nowUnix + IntervalsSeconds[stage]);
    }

    public static bool TryParseGrade(string? wire, out ReviewGrade grade)
    {
        switch ((wire ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "forgot": grade = ReviewGrade.Forgot; return true;
            case "fuzzy": grade = ReviewGrade.Fuzzy; return true;
            case "familiar": grade = ReviewGrade.Familiar; return true;
            case "known": grade = ReviewGrade.Known; return true;
            default: grade = ReviewGrade.Forgot; return false;
        }
    }

    public static string ToWire(ReviewGrade grade) => grade switch
    {
        ReviewGrade.Forgot => "forgot",
        ReviewGrade.Fuzzy => "fuzzy",
        ReviewGrade.Familiar => "familiar",
        _ => "known",
    };

    /// <summary>The little caption under each grade button ("7 天后"), derived locally so it shows before any request.</summary>
    public static string IntervalLabel(int stage) => Labels[Math.Clamp(stage, 0, MaxStage)];

    public static string Display(ReviewGrade grade) => GradeLabels[Math.Clamp((int)grade, 0, 3)];
}
