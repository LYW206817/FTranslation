namespace FastTranslation.Server.Domain;

/// <summary>
/// Decides which shelf a saved pair belongs on when the client did not say. The rules are
/// deliberately blunt (design §4.2): a learner can always re-file an item from 单词 to 短语,
/// so a wrong guess costs one PATCH, while a clever guess that surprises people costs trust.
/// </summary>
public static class NoteClassifier
{
    /// <summary>A "word" longer than this is almost always a mis-typed phrase.</summary>
    public const int MaxWordLength = 24;

    /// <summary>Beyond this many words the text is a sentence even without terminal punctuation.</summary>
    public const int MaxPhraseWords = 6;

    /// <summary>Longer than this is treated as a sentence regardless of word count.</summary>
    public const int MaxPhraseLength = 80;

    public static NoteKind Classify(string? source)
    {
        var text = (source ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return NoteKind.Sentence;
        }

        // Terminal punctuation wins over the "no spaces" test: "Really?" and "她走了。" carry no
        // whitespace either, and filing them as words would be plainly wrong to a learner.
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
