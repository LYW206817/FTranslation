namespace FastTranslation.Core.Notes;

/// <summary>
/// 单词本的三个书架。同一张表按这个值过滤——已保存的条目可以从「单词」改归到「短语」，
/// 而不必删了重存（设计 §4.3）。
/// </summary>
public enum NoteKind
{
    Word = 0,
    Phrase = 1,
    Sentence = 2,
}

public static class NoteKinds
{
    public static string ToWire(NoteKind kind) => kind switch
    {
        NoteKind.Word => "word",
        NoteKind.Phrase => "phrase",
        _ => "sentence",
    };

    public static bool TryParse(string? wire, out NoteKind kind)
    {
        switch ((wire ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "word": kind = NoteKind.Word; return true;
            case "phrase": kind = NoteKind.Phrase; return true;
            case "sentence": kind = NoteKind.Sentence; return true;
            default: kind = NoteKind.Sentence; return false;
        }
    }

    public static string Display(NoteKind kind) => kind switch
    {
        NoteKind.Word => "单词",
        NoteKind.Phrase => "短语",
        _ => "语句",
    };
}

/// <summary>客户端视角下的一条已保存译文对（服务端时间戳为 unix 秒）。</summary>
public sealed record Note(
    Guid Id,
    NoteKind Kind,
    string SourceText,
    string TargetText,
    string SourceLang,
    string TargetLang,
    string Model,
    string OriginApp,
    string Annotation,
    long CreatedAt,
    long UpdatedAt,
    int Stage = 0,
    long DueAt = 0,
    string LastGrade = "",
    int ReviewCount = 0,
    int Lapses = 0,
    long ReviewedAt = 0)
{
    public string KindDisplay => NoteKinds.Display(Kind);

    /// <summary>"上次:忘记 / 上次:模糊 / 上次:熟悉 / 上次:记住 / 还没评过"——详情面板的状态行。</summary>
    public string GradeDisplay => LastGrade switch
    {
        "forgot" => "上次:忘记",
        "fuzzy" => "上次:模糊",
        "familiar" => "上次:熟悉",
        "known" => "上次:记住",
        _ => "还没评过",
    };

    /// <summary>"未排期 / 今天到期 / 明天 / 3 天后"——列表行的第三行。</summary>
    public string DueLine
    {
        get
        {
            if (Stage == 0) return "未排期";
            var today = DateTimeOffset.Now.Date;
            var due = DateTimeOffset.FromUnixTimeSeconds(DueAt).ToLocalTime().Date;
            var days = (due - today).Days;
            return days switch
            {
                <= 0 => "今天到期",
                1 => "明天",
                _ => days + " 天后",
            };
        }
    }

    /// <summary>卡片今天到期或已逾期时为 true——列表会给这一行着色，提示「打开我」。</summary>
    public bool DueNow
    {
        get
        {
            if (Stage == 0 || DueAt == 0) return false;
            var today = DateTimeOffset.Now.Date;
            return DateTimeOffset.FromUnixTimeSeconds(DueAt).ToLocalTime().Date <= today;
        }
    }

    /// <summary>"chrome · 10-28 · 今天到期"——来源行加排期，绝不用窗口标题。</summary>
    public string MetaLine => OriginLine + " · " + DueLine;

    /// <summary>"chrome · 10-28"——卡片下方的来源行，绝不用窗口标题。</summary>
    public string OriginLine
    {
        get
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(CreatedAt).ToLocalTime().ToString("MM-dd");
            return string.IsNullOrWhiteSpace(OriginApp) ? when : OriginApp + " · " + when;
        }
    }
}

/// <summary>删除结果；用 record 而不是裸 bool，这样它能搭上通用结果类型。</summary>
public sealed record NoteDeleted(bool Deleted);

/// <summary>单词本的一页，外加上账号级总数，好让窗口显示数量。</summary>
public sealed record NotePage(IReadOnlyList<Note> Items, int Total);

/// <summary>一天的队列：按时间到期的，加上当日配额内的新卡（没学完的会留到以后）。</summary>
public sealed record ReviewQueue(
    IReadOnlyList<Note> Due,
    IReadOnlyList<Note> Fresh,
    int DueTotal,
    int FreshQuota)
{
    public IReadOnlyList<Note> All => [.. Due, .. Fresh];
    public int Count => Due.Count + Fresh.Count;
}
