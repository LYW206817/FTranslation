namespace FastTranslation.Core.Notes;

/// <summary>
/// 用户对一张卡的回忆程度。顺序有意义：值越大，卡片排得越远。
/// 线上字段名是 API 契约，不可改动。
/// </summary>
public enum ReviewGrade
{
    Forgot = 0,
    Fuzzy = 1,
    Familiar = 2,
    Known = 3,
}

/// <summary>
/// 固定阶梯，不是 SM-2。产品要的是「分个类，过阵子再拿出来」，两个计数器加一张查表就能
/// 精确回答。只做纯函数——不碰时钟、不碰 IO——这样转移表不需要 fixture 也能测。
/// </summary>
public static class ReviewSchedule
{
    public const int MaxStage = 7;

    /// <summary>评分后等待的秒数，按结果阶段索引。索引 0 表示「从未评分」。</summary>
    public static readonly IReadOnlyList<long> IntervalsSeconds = new long[]
    {
        0L, 600L, 86_400L, 259_200L, 604_800L, 1_209_600L, 2_419_200L, 4_838_400L,
    };

    /// <summary>四个按钮从左到右的显示顺序。</summary>
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
    /// 四个评分对应四个档位。「记住」首次就跳到 7 天后（用户已经会了，
    /// 没有理由今天再出现一次），之后每次成功逐档上移，最高到 56 天。
    /// 所有降档的下限都是阶段 1：阶段 0 保留给「从未评分」，
    /// 所以被遗忘的卡不会被降回新卡堆。
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

    /// <summary>结果阶段，以及用 unix 秒表示的绝对到期时间。</summary>
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

    /// <summary>每个评分按钮下方的小字说明（"7 天后"），本地推导，无需请求即可显示。</summary>
    public static string IntervalLabel(int stage) => Labels[Math.Clamp(stage, 0, MaxStage)];

    public static string Display(ReviewGrade grade) => GradeLabels[Math.Clamp((int)grade, 0, 3)];
}
