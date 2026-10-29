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

        if (IsSingleCjkCharacter(text) || (!text.Any(char.IsWhiteSpace) && text.Length <= MaxWordLength))
        {
            return NoteKind.Word;
        }

        return CountWords(text) <= MaxPhraseWords ? NoteKind.Phrase : NoteKind.Sentence;
    }

    private static bool IsSingleCjkCharacter(string text) =>
        text.Length == 1 && text[0] >= 0x4E00 && text[0] <= 0x9FFF;

    private static bool EndsWithTerminalPunctuation(string text) =>
        text[^1] is '.' or '!' or '?' or '\u3002' or '\uFF01' or '\uFF1F' or '\u2026';

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
