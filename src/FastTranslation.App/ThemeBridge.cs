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
    internal static void Apply(AppTheme theme, string? accent = null)
    {
        ThemePalette.Use(theme, accent);
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
        // v68：PrimaryButton 的禁用态一直引用 HairStrongBrush，但这个键从来没被
        // 注入过(DynamicResource 找不到就取默认值=透明)，于是禁用按钮仍是蓝底。
        Set(r, "HairStrongBrush", c.HairStrong);
        Set(r, "BorderStrongBrush", c.BorderStrong);
        Set(r, "HoverBrush", c.Hover);
        Set(r, "TextPrimaryBrush", c.TextPrimary);
        Set(r, "TextSecondaryBrush", c.TextSecondary);
        Set(r, "TextMutedBrush", c.TextMuted);
        Set(r, "DangerBrush", c.Danger);
        Set(r, "OnBrandTextBrush", c.OnBrandText);
        // v69：品牌色当文字用的那一支，和 BrandBrush 分开——描边和图标继续用 Brand。
        Set(r, "BrandTextBrush", c.BrandText);

        // 四档自评：文字 / 底色 / 描边各一条，XAML 只引用键名
        SetGrade(r, "Known", c.GradeKnown, c.GradeKnownFill, c.GradeKnownBorder);
        SetGrade(r, "Familiar", c.GradeFamiliar, c.GradeFamiliarFill, c.GradeFamiliarBorder);
        SetGrade(r, "Fuzzy", c.GradeFuzzy, c.GradeFuzzyFill, c.GradeFuzzyBorder);
        SetGrade(r, "Forgot", c.GradeForgot, c.GradeForgotFill, c.GradeForgotBorder);
        // 深色主题的档位色是亮色，选中实底时必须配深字；浅色则相反。
        Set(r, "GradeSelectedTextBrush", ThemePalette.Current == AppTheme.Dark ? "#101820" : "#FFFFFF");

        // 页面底色：左上到右下。侧栏是同一套色相的更窄一段，用来和内容区分开。
        SetGradient(r, "PageGradientBrush", c.PageFrom, c.PageTo, 0.15, 0.85);
        SetGradient(r, "SidebarGradientBrush", c.SidebarFrom, c.SidebarTo, 0.0, 1.0);
        // 引文线那道窄渐变斜着走（0,0 → 0.35,1），不是上下铺满，所以单独给端点。
        SetGradient(r, "BoltRuleBrush", c.BoltFrom, c.BoltTo, new Point(0, 0), new Point(0.35, 1));
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

    /// <summary>一档颜色：默认浅底，悬停时向档位色加深 20%；选中态在样式里用实色。</summary>
    private static void SetGrade(ResourceDictionary resources, string name, string grade, string fill, string border)
    {
        Set(resources, $"Grade{name}Brush", grade);
        Set(resources, $"Grade{name}FillBrush", fill);
        Set(resources, $"Grade{name}BorderBrush", border);
        var from = (Color)ColorConverter.ConvertFromString(fill);
        var to = (Color)ColorConverter.ConvertFromString(grade);
        var hover = Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * 0.20),
            (byte)(from.G + (to.G - from.G) * 0.20),
            (byte)(from.B + (to.B - from.B) * 0.20));
        var brush = new SolidColorBrush(hover);
        brush.Freeze();
        resources[$"Grade{name}HoverBrush"] = brush;
    }

    /// <summary>
    /// 组装一支两点线性渐变。起点/终点是相对坐标：页面用 0.15→0.85 让转角处的
    /// 色带更缓，侧栏用 0→1 铺满整列高度。
    /// </summary>
    private static void SetGradient(
        ResourceDictionary resources, string key, string fromHex, string toHex, double y1, double y2)
        => SetGradient(resources, key, fromHex, toHex, new Point(0, y1), new Point(1, y2));

    private static void SetGradient(
        ResourceDictionary resources, string key, string fromHex, string toHex, Point start, Point end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
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
