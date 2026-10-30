namespace FastTranslation.Core.Notes;

/// <summary>
/// 猜一对译文该放进哪个书架，这样点「保存」就不必多一步选择。规则是刻意粗糙的（设计 §4.2）：
/// 学习者一次 PATCH 就能重新归类，而一个让人意外的「聪明」猜测会消耗信任。
/// 与服务端分类器保持一致——服务端仍是权威。
/// </summary>
public static class NoteClassifier
{
    public const int MaxWordLength = 24;
    public const int MaxPhraseWords = 6;
    public const int MaxPhraseLength = 80;

    // 中文没有空格，上面那条「不含空白且不超长就是单词」的规则会把整句话判成单词
    //（v63：用户保存「她安静地离开了房间」被放进单词本）。改成按汉字字数分档：
    // 1–3 字是词（苹果、图书馆），4–8 字是短语/成语（画蛇添足、无风不起浪），
    // 再长的一律当句子——中文句子常常十来个字且不带标点（「她安静地离开了房间」）。
    public const int MaxCjkWordChars = 3;
    public const int MaxCjkPhraseChars = 8;

    public static NoteKind Classify(string? source)
    {
        var text = (source ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return NoteKind.Sentence;
        }

        // 句末标点优先于「没有空格」这条判断："Really?" 和 "她走了。" 同样不含空格，
        // 把它们归为单词对学习者来说是明显错误的。
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
            if (c >= '\u4E00' && c <= '\u9FFF') count++;
        }
        return count;
    }

    private static bool ContainsClauseSeparator(string text) =>
        text.Any(c => c is '\uFF0C' or ',' or '\u3001' or '\uFF1B' or ';' or '\uFF1A' or ':');

    private static bool EndsWithTerminalPunctuation(string text) =>
        text[^1] is '.' or '!' or '?' or '\u3002' or '\uFF01' or '\uFF1F' or '\u2026';

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
