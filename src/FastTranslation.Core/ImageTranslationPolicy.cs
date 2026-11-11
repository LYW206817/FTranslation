namespace FastTranslation.Core;

/// <summary>
/// 图片翻译的语言策略：AppSettings 里的语言码（可能是 "auto"）→ qwen-mt-image 需要的英文语言名。
/// 目标为「自动(中英互译)」时按 OCR 原文判方向（中文占主导 → English，其余 → Chinese），与文字链路的
/// <see cref="LanguagePolicy.DetectTarget"/> 同一条规则；没有可信原文（OCR 失败）才回落 Chinese。
/// 接口要求 source_lang 与 target_lang 不能相同，因此同语言时 source 退回 "auto"。
/// </summary>
public static class ImageTranslationPolicy
{
    public const string AutoLanguage = "auto";
    public const string DefaultTargetLanguage = "Chinese";

    /// <param name="sampleText">原图里 OCR 出来的原文，仅用于目标为 auto 时判方向；显式目标语言可传 null。</param>
    public static (string Source, string Target) Resolve(string? settingsSource, string? settingsTarget, string? sampleText = null)
    {
        var target = IsAuto(settingsTarget) ? ResolveAutoTarget(sampleText) : LanguageCatalog.ToApiName(settingsTarget!);
        var source = IsAuto(settingsSource) ? AutoLanguage : LanguageCatalog.ToApiName(settingsSource!);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            source = AutoLanguage;
        }
        return (source, target);
    }

    /// <summary>
    /// v126: 原来 auto 固定解析成 Chinese，中文截图会被要求译成中文——图片模型发现源与目标同语言就
    /// 原样返回，用户看到的是「截图翻译没变化」。方向只能从原文判，不能只看设置。
    /// </summary>
    private static string ResolveAutoTarget(string? sampleText)
    {
        if (string.IsNullOrWhiteSpace(sampleText)) return DefaultTargetLanguage;
        var targetCode = LanguagePolicy.DetectTarget(TextNormalization.Normalize(sampleText), AutoLanguage);
        return LanguageCatalog.ToApiName(targetCode);
    }

    public static bool IsAuto(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Trim().Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase);
}
