using System.Globalization;
using System.Text;

namespace FastTranslation.Core;

public static class TextNormalization
{
    /// <summary>Collapse whitespace/format chars, trim; empty when nothing translatable remains.</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        var pendingSpace = false;
        foreach (var ch in input)
        {
            if (IsDropChar(ch)) continue;
            if (char.IsWhiteSpace(ch) || IsSpaceLike(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    public static bool IsMeaningful(string normalized) => normalized.Length > 0 && normalized.Any(c => !char.IsPunctuation(c));

    private static bool IsDropChar(char c) => char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.Format => true,          // Cf: ZWJ/BOM/soft hyphen markers
        UnicodeCategory.Control => c is '\r' or '\n' or '\t' ? false : true,
        UnicodeCategory.Surrogate => false,
        UnicodeCategory.PrivateUse => true,
        _ => false,
    };

    private static bool IsSpaceLike(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator
        && c != '\u00A0'; // keep NBSP meaningful? drop it as space: treat as space actually
}

public static class LanguagePolicy
{
    /// <summary>Detect source intent: CJK-dominant text translates to English, anything else to Chinese.</summary>
    public static string DetectTarget(string normalized, string userPreference)
    {
        if (userPreference != "auto") return userPreference;
        var cjk = 0; var total = 0;
        foreach (var ch in normalized)
        {
            if (!char.IsLetter(ch)) continue;
            total++;
            if (IsCjk(ch)) cjk++;
        }
        if (total == 0) return LanguageCatalog.ChineseCode;
        return cjk * 2 >= total ? LanguageCatalog.EnglishCode : LanguageCatalog.ChineseCode;
    }

    public static bool IsCjk(char c) => c is >= '\u4E00' and <= '\u9FFF'
        or >= '\u3400' and <= '\u4DBF'
        or >= '\uF900' and <= '\uFAFF'
        or >= '\u3040' and <= '\u30FF'; // kana counts as "not Chinese target" edge — treat as CJK block for ratio only

    public static (string Source, string Target) Resolve(string sourcePreference, string targetPreference, string normalized)
    {
        var target = DetectTarget(normalized, targetPreference);
        var source = sourcePreference == "auto"
            ? (target == LanguageCatalog.ChineseCode ? LanguageCatalog.EnglishCode : LanguageCatalog.ChineseCode)
            : sourcePreference;
        // If user forces a single target language, "auto" source just means "the other one" is wrong;
        // Qwen accepts explicit or "auto" — send explicit when we know it, else auto.
        return (source, target);
    }
}
