namespace FastTranslation.Platform.Windows;

/// <summary>Resolved appearance. The setting is "system" | "light" | "dark"; a resolved value is
/// only ever Light or Dark.</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>One complete palette. Hex strings are the single source of truth: App.xaml resource
/// brushes are built from them, and the overlay surfaces (which cannot use XAML resources) build
/// their frozen brushes from the same values.</summary>
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
/// Light/dark palettes plus the startup-time decision of which one to use.
/// StaticResource is resolved when a control is loaded, so picking the palette before the first
/// window is created switches the whole UI without a single XAML change (v24).
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

    /// <summary>Palette in effect for surfaces created from now on.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static ThemeColors Active => Current == AppTheme.Dark ? Dark : Light;

    /// <summary>Maps a stored setting to a concrete theme. Unknown values fall back to the system.</summary>
    public static AppTheme Resolve(string? setting, bool systemPrefersDark) => setting?.Trim().ToLowerInvariant() switch
    {
        LightSetting => AppTheme.Light,
        DarkSetting => AppTheme.Dark,
        _ => systemPrefersDark ? AppTheme.Dark : AppTheme.Light,
    };

    public static AppTheme Resolve(string? setting) => Resolve(setting, SystemPrefersDark());

    /// <summary>
    /// Reads HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme.
    /// Missing key or unreadable value means light — the conservative choice for a tray app whose
    /// window must stay legible.
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

    /// <summary>Must run before the first window is constructed (windows parse StaticResource at load).</summary>
    public static void Use(AppTheme theme) => Current = theme;
}
