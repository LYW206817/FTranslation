namespace FastTranslation.Server.Domain;

/// <summary>
/// 客户端未指定时，决定保存的词对属于哪个书架。这些规则
/// 刻意保持粗放（设计 §4.2）：学习者随时可以把条目从单词改归到短语，
/// 所以猜错只多一次 PATCH，而猜得“聪明”却让人意外则要付出信任代价。
/// 规则必须与 FastTranslation.Core.Notes.NoteClassifier 保持一致——
/// 客户端现在总会带上自己的判断，这里只是兜底。
/// </summary>
public static class NoteClassifier
{
    /// <summary>超过这个长度的“单词”几乎总是误录的短语。</summary>
    public const int MaxWordLength = 24;

    /// <summary>词数超过这个数，即使没有句末标点也按句子处理。</summary>
    public const int MaxPhraseWords = 6;

    /// <summary>超过这个长度时，不论词数多少都按句子处理。</summary>
    public const int MaxPhraseLength = 80;

    /// <summary>汉字个数不超过这个数才算“词”。中文没有空格，光看空白判不出来。</summary>
    public const int MaxCjkWordChars = 3;

    /// <summary>汉字个数不超过这个数（且无逗号分句）算“短语”。</summary>
    public const int MaxCjkPhraseChars = 8;

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

        var cjkCount = CountCjk(text);
        if (cjkCount > 0)
        {
            return ClassifyCjk(text, cjkCount);
        }

        if (!text.Any(char.IsWhiteSpace) && text.Length <= MaxWordLength)
        {
            return NoteKind.Word;
        }

        return CountWords(text) <= MaxPhraseWords ? NoteKind.Phrase : NoteKind.Sentence;
    }

    private static NoteKind ClassifyCjk(string text, int cjkCount)
    {
        // 带逗号/分号的是一串小句，不论长短都不是一个词条
        if (ContainsClauseSeparator(text))
        {
            return NoteKind.Sentence;
        }

        if (cjkCount <= MaxCjkWordChars)
        {
            // 「API 失败」这种中英混排虽然只有两个汉字，但它是两段并列的东西，不是单个词
            return text.Any(char.IsWhiteSpace) ? NoteKind.Phrase : NoteKind.Word;
        }

        return cjkCount <= MaxCjkPhraseChars ? NoteKind.Phrase : NoteKind.Sentence;
    }

    private static int CountCjk(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) count++;
        }
        return count;
    }

    private static bool ContainsClauseSeparator(string text) =>
        text.Any(c => c is '，' or ',' or '、' or '；' or ';' or '：' or ':');

    private static bool EndsWithTerminalPunctuation(string text) =>
        text[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
