namespace FastTranslation.Core;

/// <summary>
/// 图片翻译的语言策略：AppSettings 里的语言码（可能是 "auto"）→ qwen-mt-image 需要的英文语言名。
/// 接口要求 source_lang 与 target_lang 不能相同，因此同语言时 source 退回 "auto"。
/// </summary>
public static class ImageTranslationPolicy
{
    public const string AutoLanguage = "auto";
    public const string DefaultTargetLanguage = "Chinese";

    public static (string Source, string Target) Resolve(string? settingsSource, string? settingsTarget)
    {
        var target = IsAuto(settingsTarget) ? DefaultTargetLanguage : LanguageCatalog.ToApiName(settingsTarget!);
        var source = IsAuto(settingsSource) ? AutoLanguage : LanguageCatalog.ToApiName(settingsSource!);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            source = AutoLanguage;
        }
        return (source, target);
    }

    private static bool IsAuto(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Trim().Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase);
}
