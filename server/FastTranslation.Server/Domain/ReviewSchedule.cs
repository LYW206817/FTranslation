namespace FastTranslation.Server.Domain;

/// <summary>
/// How well the user recalled a card. Mirrors <c>FastTranslation.Core.Notes.ReviewGrade</c> — the
/// server has no reference to the client's Core project, and the same applies to NoteKind and
/// NoteClassifier. The server copy is authoritative: the client only needs the labels.
/// </summary>
public enum ReviewGrade
{
    Forgot = 0,
    Fuzzy = 1,
    Familiar = 2,
    Known = 3,
}

/// <summary>
/// The review ladder. Mirrors <c>FastTranslation.Core.Notes.ReviewSchedule</c>; both copies must
/// agree on the table below, which is a product constant (design §4.1) rather than a tuning knob.
/// </summary>
public static class ReviewSchedule
{
    public const int MaxStage = 7;

    /// <summary>Seconds to wait after grading, indexed by the resulting stage. Index 0 means "never graded".</summary>
    public static readonly IReadOnlyList<long> IntervalsSeconds = new long[]
    {
        0L, 600L, 86_400L, 259_200L, 604_800L, 1_209_600L, 2_419_200L, 4_838_400L,
    };

    /// <summary>
    /// Four grades map onto four rungs. 记住 jumps to 7 days on the first try and then walks up one
    /// rung per success up to 56 days. Every downgrade has a floor of stage 1: stage 0 stays reserved
    /// for "never graded", so a forgotten card is never demoted back into the new-card pile.
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
}
