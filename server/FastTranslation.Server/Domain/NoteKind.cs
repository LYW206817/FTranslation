namespace FastTranslation.Server.Domain;

/// <summary>
/// The three shelves of the notebook are one table filtered by this value, not three tables:
/// a saved item can be re-filed from 单词 to 短语 without losing its history (design §4.3).
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
}
