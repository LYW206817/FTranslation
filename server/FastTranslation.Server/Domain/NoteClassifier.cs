namespace FastTranslation.Server.Domain;

/// <summary>
/// 客户端未指定时，决定保存的词对属于哪个书架。这些规则
/// 刻意保持粗放（设计 §4.2）：学习者随时可以把条目从单词改归到短语，
/// 所以猜错只多一次 PATCH，而猜得“聪明”却让人意外则要付出信任代价。
/// </summary>
public static class NoteClassifier
{
    /// <summary>超过这个长度的“单词”几乎总是误录的短语。</summary>
    public const int MaxWordLength = 24;

    /// <summary>词数超过这个数，即使没有句末标点也按句子处理。</summary>
    public const int MaxPhraseWords = 6;

    /// <summary>超过这个长度时，不论词数多少都按句子处理。</summary>
    public const int MaxPhraseLength = 80;

    public static NoteKind Classify(string? source)
    {
        var text = (source ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return NoteKind.Sentence;
        }

        // 句末标点优先于“无空格”判据：“Really?” 和“她走了。”同样不含空格，
        // 把它们归为单词在学习者看来显然错误。
        if (EndsWithTerminalPunctuation(text) || text.Length > MaxPhraseLength)
        {
            return NoteKind.Sentence;
        }

        if (IsSingleCjkCharacter(text) || (!text.Any(char.IsWhiteSpace) && text.Length <= MaxWordLength))
        {
            return NoteKind.Word;
        }

        return CountWords(text) <= MaxPhraseWords ? NoteKind.Phrase : NoteKind.Sentence;
    }

    private static bool IsSingleCjkCharacter(string text) =>
        text.Length == 1 && text[0] >= 0x4E00 && text[0] <= 0x9FFF;

    private static bool EndsWithTerminalPunctuation(string text) =>
        text[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
