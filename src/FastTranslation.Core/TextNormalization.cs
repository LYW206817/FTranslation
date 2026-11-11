using System.Globalization;
using System.Text;

namespace FastTranslation.Core;

public static class TextNormalization
{
    /// <summary>压缩空白/格式字符并去掉首尾空白；没有可翻译内容时返回空串。</summary>
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
        UnicodeCategory.Format => true,          // Cf：ZWJ/BOM/软连字符等标记
        UnicodeCategory.Control => c is '\r' or '\n' or '\t' ? false : true,
        UnicodeCategory.Surrogate => false,
        UnicodeCategory.PrivateUse => true,
        _ => false,
    };

    private static bool IsSpaceLike(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator
        && c != '\u00A0'; // 保留 NBSP 的原义吗？还是当空格丢掉：其实按空格处理
}

public static class LanguagePolicy
{
    /// <summary>推断目标语言：中文占主导的文本译成英文，其余译成中文。</summary>
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
        or >= '\u3040' and <= '\u30FF'; // 假名归入「不译成中文」的边界情况——只用于计算 CJK 占比

    public static (string Source, string Target) Resolve(string sourcePreference, string targetPreference, string normalized)
    {
        var target = DetectTarget(normalized, targetPreference);
        // 用户指定了目标语言时，源语言无从推断：英文文本 + 目标日语若硬发 Chinese，
        // 请求方向与原文相反，qwen-mt 会按中文处理英文输入。只有「自动目标」的中英互译
        // 才能由目标语言反推源语言。
        var source = sourcePreference != "auto"
            ? sourcePreference
            : targetPreference == "auto"
                ? (target == LanguageCatalog.ChineseCode ? LanguageCatalog.EnglishCode : LanguageCatalog.ChineseCode)
                : "auto";
        return (source, target);
    }
}
