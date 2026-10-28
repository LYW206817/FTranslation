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
    long UpdatedAt)
{
    public string KindDisplay => NoteKinds.Display(Kind);

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
