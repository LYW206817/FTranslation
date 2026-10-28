namespace FastTranslation.Core.Notes;

/// <summary>
/// Guesses which shelf a pair belongs on, so 保存 needs no extra clicks. The rules are blunt on
/// purpose (design §4.2): a learner can re-file anything in one PATCH, while a clever guess that
/// surprises people costs trust. Mirrors the server's classifier — the server stays authoritative.
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
        text[^1] is '.' or '!' or '?' or '\u3002' or '\uFF01' or '\uFF1F' or '\u2026';

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
