using System.Windows;
using System.Windows.Media;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// Copies the resolved palette into App.xaml resources at startup (v24).
///
/// Every brush reference in the XAML is a `{DynamicResource …Brush}`, so swapping an entry here
/// would in principle be enough — but the brushes are also mutated **in place**, because a frozen
/// brush (XAML freezes brushes by default) would keep the light color in whatever already resolved
/// it. The tokens carry `po:Freeze="False"` for that reason; both paths stay correct.
/// </summary>
internal static class ThemeBridge
{
    internal static void Apply(AppTheme theme)
    {
        ThemePalette.Use(theme);
        var c = ThemePalette.Active;
        var r = Application.Current?.Resources;
        if (r is null) return;

        Set(r, "BrandBrush", c.Brand);
        Set(r, "BrandHoverBrush", c.BrandHover);
        Set(r, "BrandPressedBrush", c.BrandPressed);
        Set(r, "BrandFillBrush", c.BrandFill);
        Set(r, "BrandFillHoverBrush", c.BrandFillHover);
        Set(r, "AccentBrush", c.Accent);
        Set(r, "WindowBgBrush", c.WindowBg);
        Set(r, "SurfaceBrush", c.Surface);
        Set(r, "HairBrush", c.Hair);
        Set(r, "BorderStrongBrush", c.BorderStrong);
        Set(r, "HoverBrush", c.Hover);
        Set(r, "TextPrimaryBrush", c.TextPrimary);
        Set(r, "TextSecondaryBrush", c.TextSecondary);
        Set(r, "TextMutedBrush", c.TextMuted);
        Set(r, "DangerBrush", c.Danger);
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        if (resources[key] is SolidColorBrush existing)
        {
            // a frozen XAML brush cannot be mutated; replace it and let the element-level
            // StaticResource lookups (which happen at window load, i.e. later) pick it up
            if (!existing.IsFrozen && existing.Color != color)
            {
                existing.Color = color;
                return;
            }
            if (existing.Color == color) return;
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
