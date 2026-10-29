namespace FastTranslation.Core.Notes;

/// <summary>
/// The three shelves of the notebook. One table filtered by this value — a saved item can be
/// re-filed from 单词 to 短语 without being re-saved (design §4.3).
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

/// <summary>One saved pair as the client sees it (server timestamps are unix seconds).</summary>
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

    /// <summary>"上次:忘记 / 上次:模糊 / 上次:熟悉 / 上次:记住 / 还没评过" — detail-pane status line.</summary>
    public string GradeDisplay => LastGrade switch
    {
        "forgot" => "上次:忘记",
        "fuzzy" => "上次:模糊",
        "familiar" => "上次:熟悉",
        "known" => "上次:记住",
        _ => "还没评过",
    };

    /// <summary>"未排期 / 今天到期 / 明天 / 3 天后" — third line of a list row.</summary>
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

    /// <summary>True when the card is due today or overdue — the list tints that line to say "open me".</summary>
    public bool DueNow
    {
        get
        {
            if (Stage == 0 || DueAt == 0) return false;
            var today = DateTimeOffset.Now.Date;
            return DateTimeOffset.FromUnixTimeSeconds(DueAt).ToLocalTime().Date <= today;
        }
    }

    /// <summary>"chrome · 10-28 · 今天到期" — the provenance line plus the schedule, never a window title.</summary>
    public string MetaLine => OriginLine + " · " + DueLine;

    /// <summary>"chrome · 10-28" — the provenance line under a card, never the window title.</summary>
    public string OriginLine
    {
        get
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(CreatedAt).ToLocalTime().ToString("MM-dd");
            return string.IsNullOrWhiteSpace(OriginApp) ? when : OriginApp + " · " + when;
        }
    }
}

/// <summary>Result of a delete; a record rather than a bare bool so it can ride the generic result type.</summary>
public sealed record NoteDeleted(bool Deleted);

/// <summary>One page of the notebook plus the account-wide total so the window can show a count.</summary>
public sealed record NotePage(IReadOnlyList<Note> Items, int Total);

/// <summary>One day's queue: what is due by time plus what is new under today's quota.</summary>
public sealed record ReviewQueue(
    IReadOnlyList<Note> Due,
    IReadOnlyList<Note> Fresh,
    int DueTotal,
    int FreshQuota)
{
    public IReadOnlyList<Note> All => [.. Due, .. Fresh];
    public int Count => Due.Count + Fresh.Count;
}
