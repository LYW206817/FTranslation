namespace FastTranslation.Platform.Windows;

/// <summary>解析后的外观。设置值是 "system" | "light" | "dark"；解析结果只会是
/// Light 或 Dark。</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>一整套调色板。十六进制字符串是唯一数据源：App.xaml 里的资源
/// 画刷由它们生成，浮层界面(用不了 XAML 资源)也用同一批值构建自己的冻结画刷。</summary>
public sealed record ThemeColors(
    string Brand,
    string BrandHover,
    string BrandPressed,
    string BrandFill,
    string BrandFillHover,
    // v68：压在品牌色实底上的字色。深色主题的品牌蓝偏亮(#4A82FF)，白字只有
    // 3.54:1；换成近黑墨色才是 5.05:1。浅色主题仍是白字(4.92:1)。
    string OnBrandText,
    string Accent,
    string WindowBg,
    string Surface,
    string Hair,
    string HairStrong,
    string BorderStrong,
    string Hover,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string Danger,
    string Notice,
    // v60：四档自评各有一套颜色，靠色相区分「记住 / 熟悉 / 模糊 / 忘记」，
    // 而不是只靠深浅。Fill 是按钮底色，Border 是描边，Fg 是文字。
    string GradeKnown,
    string GradeKnownFill,
    string GradeKnownBorder,
    string GradeFamiliar,
    string GradeFamiliarFill,
    string GradeFamiliarBorder,
    string GradeFuzzy,
    string GradeFuzzyFill,
    string GradeFuzzyBorder,
    string GradeForgot,
    string GradeForgotFill,
    string GradeForgotBorder,
    // 页面底色：左上「珠光白」到右下「雾蓝」。两个端点分别给出，渐变由 ThemeBridge 组装。
    string PageFrom,
    string PageTo,
    string SidebarFrom,
    string SidebarTo,
    // 悬停抬起时那层很淡的投影（#AARRGGBB）
    string Shadow);

/// <summary>
/// 浅色/深色调色板，以及启动时决定用哪一套。
/// StaticResource 在控件加载时才解析，因此在第一个窗口创建之前选定调色板，
/// 无需改动任何一行 XAML 就能切换整个 UI(v24)。
/// </summary>
public static class ThemePalette
{
    public const string SystemSetting = "system";
    public const string LightSetting = "light";
    public const string DarkSetting = "dark";

    public static ThemeColors Light { get; } = new(
        Brand: "#2562FF",
        BrandHover: "#1D4FE0",
        BrandPressed: "#1A45C4",
        BrandFill: "#EDF2FF",
        BrandFillHover: "#E0EAFF",
        OnBrandText: "#FFFFFF",
        Accent: "#40A9FF",
        WindowBg: "#F7F9FC",
        Surface: "#FFFFFF",
        Hair: "#E4EAF3",
        HairStrong: "#D9E3F1",
        BorderStrong: "#CFD9EA",
        Hover: "#F0F4FA",
        TextPrimary: "#1B2536",
        TextSecondary: "#5C6B82",
        // v68：#8B97A9 对 WindowBg 只有 2.81:1、对 Surface 2.96:1，全仓 38 处引用
        // 都是发虚的提示文字；#667287 是 4.61 / 4.86。
        TextMuted: "#667287",
        Danger: "#DC2626",
        Notice: "#C2410C",
        GradeKnown: "#0E7C66",
        GradeKnownFill: "#E7F5F0",
        GradeKnownBorder: "#B9E2D6",
        GradeFamiliar: "#35648F",
        GradeFamiliarFill: "#E9F0F8",
        GradeFamiliarBorder: "#C3D7EA",
        GradeFuzzy: "#A9611A",
        GradeFuzzyFill: "#FBF0E0",
        GradeFuzzyBorder: "#EFD8B4",
        GradeForgot: "#A9453B",
        GradeForgotFill: "#FAEAE7",
        GradeForgotBorder: "#EDCBC4",
        PageFrom: "#FDFEFF",
        PageTo: "#E7EEF9",
        SidebarFrom: "#FAFCFF",
        SidebarTo: "#F0F5FC",
        Shadow: "#1F1B2536");

    public static ThemeColors Dark { get; } = new(
        Brand: "#4A82FF",
        BrandHover: "#6A9BFF",
        BrandPressed: "#3A6FE0",
        BrandFill: "#22304D",
        BrandFillHover: "#2B3C60",
        OnBrandText: "#101820",
        Accent: "#5BB4FF",
        WindowBg: "#16191F",
        Surface: "#1F242C",
        Hair: "#2B313B",
        HairStrong: "#343B46",
        BorderStrong: "#3A424F",
        Hover: "#262C35",
        TextPrimary: "#E8ECF3",
        TextSecondary: "#A3AEBF",
        // v68：#7C8798 对 Surface #1F242C 是 4.29:1，差一点点；#8C97A8 是 5.28。
        TextMuted: "#8C97A8",
        Danger: "#FF6B6B",
        Notice: "#FF9A5C",
        GradeKnown: "#56CFAF",
        GradeKnownFill: "#123029",
        GradeKnownBorder: "#23564A",
        GradeFamiliar: "#7FB2DC",
        GradeFamiliarFill: "#172837",
        GradeFamiliarBorder: "#27445E",
        GradeFuzzy: "#E8B26A",
        GradeFuzzyFill: "#2E2417",
        GradeFuzzyBorder: "#4E3D22",
        GradeForgot: "#E38F84",
        GradeForgotFill: "#2F1F1D",
        GradeForgotBorder: "#54332E",
        PageFrom: "#121620",
        PageTo: "#1B2431",
        SidebarFrom: "#171C25",
        SidebarTo: "#1B212B",
        Shadow: "#99000000");

    /// <summary>从此刻起创建的界面所使用的调色板。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static ThemeColors Active => Current == AppTheme.Dark ? Dark : Light;

    /// <summary>把存储的设置值映射为具体主题。未知值回退到跟随系统。</summary>
    public static AppTheme Resolve(string? setting, bool systemPrefersDark) => setting?.Trim().ToLowerInvariant() switch
    {
        LightSetting => AppTheme.Light,
        DarkSetting => AppTheme.Dark,
        _ => systemPrefersDark ? AppTheme.Dark : AppTheme.Light,
    };

    public static AppTheme Resolve(string? setting) => Resolve(setting, SystemPrefersDark());

    /// <summary>
    /// 读取 HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme。
    /// 键缺失或值不可读时视为浅色——对托盘应用来说这是保守选择，窗口必须保持可读。
    /// </summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>必须在第一个窗口构造之前调用(窗口加载时会解析 StaticResource)。</summary>
    public static void Use(AppTheme theme) => Current = theme;
}
