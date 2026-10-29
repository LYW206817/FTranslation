using System.Windows;
using System.Windows.Media;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 启动时把解析好的调色板写入 App.xaml 资源（v24）。
///
/// XAML 里每处画刷引用都是 `{DynamicResource …Brush}`，所以原则上换掉这里的条目就够了，
/// 但这些画刷还会被**就地修改**：冻结的画刷（XAML 默认就会冻结画刷）会把浅色一直留在
/// 已经解析过它的元素上。这些 token 因此带着 `po:Freeze="False"`；
/// 于是换条目与就地改色两条路径都仍然成立。
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
            // 冻结的 XAML 画刷无法修改；替换它，让元素级的 StaticResource 查找
            //（发生在窗口加载时，也就是更晚）能取到新值
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
