namespace FastTranslation.Server.Domain;

/// <summary>
/// 用户对卡片的回忆程度。与 <c>FastTranslation.Core.Notes.ReviewGrade</c> 对应——
/// 服务端不引用客户端的 Core 项目，NoteKind 与
/// NoteClassifier 同理。以服务端这份为准：客户端只需要标签。
/// </summary>
public enum ReviewGrade
{
    Forgot = 0,
    Fuzzy = 1,
    Familiar = 2,
    Known = 3,
}

/// <summary>
/// 复习阶梯。与 <c>FastTranslation.Core.Notes.ReviewSchedule</c> 对应；两份拷贝必须
/// 在下表上保持一致，该表是产品常量（设计 §4.1），不是可调参数。
/// </summary>
public static class ReviewSchedule
{
    public const int MaxStage = 7;

    /// <summary>评分后需要等待的秒数，按结果档位索引。索引 0 表示“从未评分”。</summary>
    public static readonly IReadOnlyList<long> IntervalsSeconds = new long[]
    {
        0L, 600L, 86_400L, 259_200L, 604_800L, 1_209_600L, 2_419_200L, 4_838_400L,
    };

    /// <summary>
    /// 四个评分对应四级台阶。“记住”（Known）首次即跳到 7 天，之后每成功一次上移
    /// 一级，最高 56 天。每次降级下限都是第 1 档：第 0 档始终保留给
    /// “从未评分”，这样遗忘的卡片永远不会被打回新卡堆。
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
