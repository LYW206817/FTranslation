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
    string Notice);

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
        Accent: "#40A9FF",
        WindowBg: "#F7F9FC",
        Surface: "#FFFFFF",
        Hair: "#E4EAF3",
        HairStrong: "#D9E3F1",
        BorderStrong: "#CFD9EA",
        Hover: "#F0F4FA",
        TextPrimary: "#1B2536",
        TextSecondary: "#5C6B82",
        TextMuted: "#8B97A9",
        Danger: "#DC2626",
        Notice: "#C2410C");

    public static ThemeColors Dark { get; } = new(
        Brand: "#4A82FF",
        BrandHover: "#6A9BFF",
        BrandPressed: "#3A6FE0",
        BrandFill: "#22304D",
        BrandFillHover: "#2B3C60",
        Accent: "#5BB4FF",
        WindowBg: "#16191F",
        Surface: "#1F242C",
        Hair: "#2B313B",
        HairStrong: "#343B46",
        BorderStrong: "#3A424F",
        Hover: "#262C35",
        TextPrimary: "#E8ECF3",
        TextSecondary: "#A3AEBF",
        TextMuted: "#7C8798",
        Danger: "#FF6B6B",
        Notice: "#FF9A5C");

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
