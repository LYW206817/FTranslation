namespace FastTranslation.Server.Domain;

/// <summary>
/// 单词本的三个书架是同一张表按该值过滤出来的，而不是三张表：
/// 已保存的条目可以从单词改归到短语，且不丢失历史记录（设计 §4.3）。
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
