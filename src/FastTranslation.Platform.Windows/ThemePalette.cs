namespace FastTranslation.Platform.Windows;

/// <summary>解析后的外观。设置值是 "system" | "light" | "dark"；解析结果只会是
/// Light 或 Dark。</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>界面强调色。与浅色/深色正交：它只替换品牌色一族，页底、文字、发丝线
/// 仍由 AppTheme 决定。旧版只有一种蓝，因此默认值就是最接近旧观感的那一支。</summary>
public enum AccentTheme
{
    /// <summary>电光蓝：偏 azure 的蓝。</summary>
    Electro,

    /// <summary>湖蓝：偏青的蓝。</summary>
    Lake,

    /// <summary>雾光紫：紫罗兰。</summary>
    Violet,
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
    // v69：品牌色当文字用时的颜色。压在白底/卡片上的品牌蓝深色只有 4.40:1、
    // 压在侧栏活动项的 BrandFill 上只有 3.71:1，都够不着 AA。这个令牌只给文字用，
    // 描边、图标、进度条仍然直接用 Brand。
    string BrandText,
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
    // v69：译文上方那道闪电引文线。深色下原来的 #2562FF 压在卡片上只有 3.17:1，
    // 换成品牌色到强调色的一段。
    string BoltFrom,
    string BoltTo,
    // 悬停抬起时那层很淡的投影（#AARRGGBB）
    string Shadow);

/// <summary>一支强调色替换掉的那一族品牌色。九项之外的颜色都沿用它所属的主题。</summary>
internal readonly record struct AccentColors(
    string Brand,
    string BrandHover,
    string BrandPressed,
    string BrandFill,
    string BrandFillHover,
    string BrandText,
    string OnBrandText,
    string BoltFrom,
    string BoltTo);

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

    public const string ElectroSetting = "electro";
    public const string LakeSetting = "lake";
    public const string VioletSetting = "violet";

    // v83：品牌蓝换成偏 azure 的电光蓝。原先的 #2562FF 是钴蓝，与强调色里的湖蓝、
    // 紫罗兰放在一起时色相区分不够。白字压 #3B6BF0 是 4.62:1（旧值 4.92:1），
    // 仍然过 AA；#4B7BFF 只有 3.77:1，因此只用在闪电渐变与色块上，不做实底按钮。
    public static ThemeColors Light { get; } = new(
        Brand: "#3B6BF0",
        BrandHover: "#2F5CD9",
        BrandPressed: "#2450E8",
        BrandFill: "#EAF0FF",
        BrandFillHover: "#DDE7FF",
        OnBrandText: "#FFFFFF",
        BrandText: "#2450E8",
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
        BoltFrom: "#4B7BFF",
        BoltTo: "#2450E8",
        Shadow: "#1F1B2536");

    public static ThemeColors Dark { get; } = new(
        Brand: "#4A82FF",
        BrandHover: "#6A9BFF",
        BrandPressed: "#3A6FE0",
        BrandFill: "#22304D",
        BrandFillHover: "#2B3C60",
        OnBrandText: "#101820",
        BrandText: "#8FB4FF",
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
        BoltFrom: "#4A82FF",
        BoltTo: "#5BB4FF",
        Shadow: "#99000000");

    // 强调色表。电光蓝不在这里：基底 Light/Dark 本身就是电光蓝——少一次 with 复制，
    // 并且 Use(Light, electro) 仍返回同一个实例（测试据此断言，见 ThemePaletteTests）。
    private static readonly AccentColors LakeLightColors = new(
        Brand: "#0E7EA8",
        BrandHover: "#0B6F94",
        BrandPressed: "#09607F",
        BrandFill: "#E4F4FA",
        BrandFillHover: "#D3EBF5",
        BrandText: "#0B6F94",
        OnBrandText: "#FFFFFF",
        BoltFrom: "#3FA9DB",
        BoltTo: "#0E7EA8");

    private static readonly AccentColors LakeDarkColors = new(
        Brand: "#4FC3F0",
        BrandHover: "#6FD2F7",
        BrandPressed: "#3AA6D0",
        BrandFill: "#123441",
        BrandFillHover: "#17414F",
        BrandText: "#8FDDF7",
        OnBrandText: "#0A1A20",
        BoltFrom: "#3FB6E0",
        BoltTo: "#7FE0F5");

    private static readonly AccentColors VioletLightColors = new(
        Brand: "#5B36F0",
        BrandHover: "#4D2BE0",
        BrandPressed: "#4322C9",
        BrandFill: "#EFEAFF",
        BrandFillHover: "#E3DBFF",
        BrandText: "#5B36F0",
        OnBrandText: "#FFFFFF",
        BoltFrom: "#8B6CFF",
        BoltTo: "#5B36F0");

    private static readonly AccentColors VioletDarkColors = new(
        Brand: "#9B7DFF",
        BrandHover: "#AE95FF",
        BrandPressed: "#8163E8",
        BrandFill: "#2A2447",
        BrandFillHover: "#342C55",
        BrandText: "#BFA9FF",
        OnBrandText: "#150E24",
        BoltFrom: "#8B6CFF",
        BoltTo: "#B79BFF");

    /// <summary>从此刻起创建的界面所使用的主题。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>当前强调色。与 <see cref="Current"/> 一起在 <see cref="Use"/> 里就位。</summary>
    public static AccentTheme CurrentAccent { get; private set; } = AccentTheme.Electro;

    /// <summary>当前生效的整套调色板＝基础主题 + 强调色。界面与浮层都必须读它，
    /// 直接读 Light/Dark 会漏掉强调色。</summary>
    public static ThemeColors Active { get; private set; } = Light;

    /// <summary>把存储的设置值映射为强调色。未知值回退到电光蓝。</summary>
    public static AccentTheme ResolveAccent(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        LakeSetting => AccentTheme.Lake,
        VioletSetting => AccentTheme.Violet,
        _ => AccentTheme.Electro,
    };

    /// <summary>强调色写回设置时用的字符串。</summary>
    public static string AccentSetting(AccentTheme accent) => accent switch
    {
        AccentTheme.Lake => LakeSetting,
        AccentTheme.Violet => VioletSetting,
        _ => ElectroSetting,
    };

    /// <summary>某支强调色在指定主题下的品牌色。设置页的色块用它显示「这一支长什么样」。</summary>
    public static string BrandOf(AppTheme theme, AccentTheme accent) => PaletteFor(theme, accent).Brand;

    /// <summary>「主题 + 强调色」组成的整套调色板。装定时不必先 <see cref="Use"/>：
    /// 设置页要把三支候选色都显示出来。</summary>
    public static ThemeColors PaletteFor(AppTheme theme, AccentTheme accent) => Compose(theme, accent);

    private static ThemeColors Compose(AppTheme theme, AccentTheme accent) => (theme, accent) switch
    {
        (AppTheme.Dark, AccentTheme.Lake) => Tint(Dark, LakeDarkColors),
        (AppTheme.Dark, AccentTheme.Violet) => Tint(Dark, VioletDarkColors),
        (AppTheme.Dark, _) => Dark,
        (_, AccentTheme.Lake) => Tint(Light, LakeLightColors),
        (_, AccentTheme.Violet) => Tint(Light, VioletLightColors),
        _ => Light,
    };

    private static ThemeColors Tint(ThemeColors palette, AccentColors accent) => palette with
    {
        Brand = accent.Brand,
        BrandHover = accent.BrandHover,
        BrandPressed = accent.BrandPressed,
        BrandFill = accent.BrandFill,
        BrandFillHover = accent.BrandFillHover,
        BrandText = accent.BrandText,
        OnBrandText = accent.OnBrandText,
        BoltFrom = accent.BoltFrom,
        BoltTo = accent.BoltTo,
    };

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
    public static void Use(AppTheme theme, string? accent = null)
    {
        Current = theme;
        CurrentAccent = ResolveAccent(accent);
        Active = Compose(theme, CurrentAccent);
    }
}
