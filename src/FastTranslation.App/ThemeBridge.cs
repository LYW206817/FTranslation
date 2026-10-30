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
///
/// v60 起这里还负责组装两支渐变（页面底色、侧栏底色）与四档自评色：
/// 渐变的两个端点必须来自调色板，XAML 里没法拼 `Color` 级的 DynamicResource。
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

        // 四档自评：文字 / 底色 / 描边各一条，XAML 只引用键名
        Set(r, "GradeKnownBrush", c.GradeKnown);
        Set(r, "GradeKnownFillBrush", c.GradeKnownFill);
        Set(r, "GradeKnownBorderBrush", c.GradeKnownBorder);
        Set(r, "GradeFamiliarBrush", c.GradeFamiliar);
        Set(r, "GradeFamiliarFillBrush", c.GradeFamiliarFill);
        Set(r, "GradeFamiliarBorderBrush", c.GradeFamiliarBorder);
        Set(r, "GradeFuzzyBrush", c.GradeFuzzy);
        Set(r, "GradeFuzzyFillBrush", c.GradeFuzzyFill);
        Set(r, "GradeFuzzyBorderBrush", c.GradeFuzzyBorder);
        Set(r, "GradeForgotBrush", c.GradeForgot);
        Set(r, "GradeForgotFillBrush", c.GradeForgotFill);
        Set(r, "GradeForgotBorderBrush", c.GradeForgotBorder);

        // 悬停抬起的投影颜色。DropShadowEffect 要的是 Color，不是 Brush。
        var shadow = (Color)ColorConverter.ConvertFromString(c.Shadow);
        r["ShadowColor"] = shadow;
        var shadowEffect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = shadow,
            BlurRadius = 14,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 1,
        };
        shadowEffect.Freeze();
        r["LiftShadow"] = shadowEffect;

        // 卡片常驻的那层更淡的投影：卡片不是可交互元素，不该和按钮抢注意力
        var cardShadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = shadow,
            BlurRadius = 18,
            ShadowDepth = 1,
            Direction = 270,
            Opacity = 0.55,
        };
        cardShadow.Freeze();
        r["CardShadow"] = cardShadow;

        // 页面底色：左上到右下。侧栏是同一套色相的更窄一段，用来和内容区分开。
        SetGradient(r, "PageGradientBrush", c.PageFrom, c.PageTo, 0.15, 0.85);
        SetGradient(r, "SidebarGradientBrush", c.SidebarFrom, c.SidebarTo, 0.0, 1.0);
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

    /// <summary>
    /// 组装一支两点线性渐变。起点/终点是相对坐标：页面用 0.15→0.85 让转角处的
    /// 色带更缓，侧栏用 0→1 铺满整列高度。
    /// </summary>
    private static void SetGradient(
        ResourceDictionary resources, string key, string fromHex, string toHex, double y1, double y2)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, y1),
            EndPoint = new Point(1, y2),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString(fromHex), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString(toHex), 1),
            },
        };
        brush.Freeze();
        resources[key] = brush;
    }
}
