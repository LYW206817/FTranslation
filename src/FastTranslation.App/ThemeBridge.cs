using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

        // v85 雾光：页底改成三段——冷雾起手、近白在中段 58% 收住、右下一角压一点暖色。
        // 两段渐变的过渡在中段附近会形成一条可见的软边，所以中段必须取「几乎无色」的那一档。
        SetGradient(
            r, "PageGradientBrush",
            (c.PageFrom, 0d), (c.PageTo, 0.58), (c.PageEnd, 1d),
            new Point(0, 0), new Point(1, 1));
        SetGradient(r, "SidebarGradientBrush", c.SidebarFrom, c.SidebarTo, 0.0, 1.0);
        // 品牌渐变只给两处用：侧栏活动项与主按钮。光从左上打进来，所以从 Brand 走到 BrandPressed。
        SetGradient(r, "BrandGradientBrush", c.Brand, c.BrandPressed, new Point(0, 0), new Point(1, 1));
        // 引文线那道窄渐变斜着走（0,0 → 0.35,1），不是上下铺满，所以单独给端点。
        SetGradient(r, "BoltRuleBrush", c.BoltFrom, c.BoltTo, new Point(0, 0), new Point(0.35, 1));
        // 窗口里那两团光：右上冷、左下暖。各自都是一个径向渐变，压得很淡，
        // 只在整片雾色底上透出来，落在卡片底下时因为卡片是不透明的而被挡住。
        SetGlow(r, "GlowBrandBrush", c.GlowBrand);
        SetGlow(r, "GlowWarmBrush", c.GlowWarm);
        Set(r, "GlassBrush", c.Glass);
        Set(r, "GlassEdgeBrush", c.GlassEdge);
        Set(r, "WarmBrush", c.Warm);
        // 投影强度的系数按主题分两档：深色底下黑影子本来就显眼，同样的 alpha 会糊成一团。
        var dark = ThemePalette.Current == AppTheme.Dark;
        SetDropShadow(r, "CardShadowEffect", c.Shadow, dark ? 0.70 : 0.85, 34, 9);
        SetDropShadow(r, "GlassShadowEffect", c.Shadow, dark ? 0.60 : 0.70, 30, 7);
        // 侧栏活动项与主按钮下面那圈品牌色辉光，颜色跟着强调色走，不能写死在 XAML 里。
        SetDropShadow(r, "NavGlowEffect", c.Brand, dark ? 0.30 : 0.34, 18, 6);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>三段线性渐变。中段的 stop 位置由调用方给，用来把冷暖两半的分界推开一点。</summary>
    private static void SetGradient(
        ResourceDictionary resources,
        string key,
        (string Hex, double At) from,
        (string Hex, double At) mid,
        (string Hex, double At) to,
        Point start,
        Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(Parse(from.Hex), from.At));
        brush.GradientStops.Add(new GradientStop(Parse(mid.Hex), mid.At));
        brush.GradientStops.Add(new GradientStop(Parse(to.Hex), to.At));
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>
    /// 径向光晕：中心偏左上，颜色在外圈渐隐成同色全透明。
    /// 用径向而不是线性，是因为它要看起来像"一团光打在窗口角上"，线性会留下一条直边。
    /// </summary>
    private static void SetGlow(ResourceDictionary resources, string key, string hex)
    {
        var color = Parse(hex);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.35, 0.35),
            GradientOrigin = new Point(0.35, 0.35),
            RadiusX = 0.68,
            RadiusY = 0.68,
        };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>
    /// 投影。颜色与 alpha 都来自 Shadow 令牌，alpha 再乘一个「这一层该有多重」的系数：
    /// 同一条 Shadow 要同时支撑窗口里的大卡、侧栏浮卡和按钮下面那圈辉光，
    /// 只用 alpha 的话三者会一样重。RenderingBias 取 Performance——这些影子铺满整窗，
    /// 质量档在大尺寸模糊上换不来肉眼可见的差别。
    /// </summary>
    private static void SetDropShadow(
        ResourceDictionary resources, string key, string hex, double weight, double blur, double depth)
    {
        var color = Parse(hex);
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(color.R, color.G, color.B),
            Opacity = color.A / 255d * weight,
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        resources[key] = effect;
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
