using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App.Pages;

/// <summary>
/// 设置作为外壳里的一个页面。控件还是旧设置窗口那一套，没动；变的是生命周期：页面只构建
/// 一次并复用，所以「把模型读进控件」必须是显式的 <see cref="ReloadFromSettings"/> 调用，
/// 而不能是构造函数的副作用。
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings;
    private readonly SettingsService _service;
    private readonly ISecretStore _secrets;
    private readonly HotkeyService _hotkey;
    private readonly Action? _checkUpdates;

    // 强调色是「待保存」的页面状态：点保存才落盘。界面本身在点选时就预览（v84），
    // 离开本页或取消则退回已保存的外观，免得预览和 settings.json 长期不一致。
    private AccentTheme _accent = AccentTheme.Electro;

    /// <summary>保存或取消之后触发，好让外壳回到用户来的地方。</summary>
    public event Action? Closed;

    public SettingsPage(
        AppSettings settings,
        SettingsService service,
        ISecretStore secrets,
        HotkeyService hotkey,
        Action? checkUpdates = null)
    {
        InitializeComponent();
        _settings = settings;
        _service = service;
        _secrets = secrets;
        _hotkey = hotkey;
        _checkUpdates = checkUpdates;
        CheckUpdateButton.IsEnabled = checkUpdates is not null;

        // 目标语言下拉框：自动 + 目录
        TargetCombo.Items.Add(new ComboBoxItem { Content = "自动(中英互译)", Tag = "auto" });
        foreach (var lang in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
        }

        AboutText.Text = AboutLine();
        ReloadFromSettings();

        // 页面是缓存的：切页面/取消离开时把预览过的外观退回已保存的那一套
        Unloaded += (_, _) => RevertAppearance();
    }

    /// <summary>
    /// 页面每次显示时由宿主调用。「取消」也会调它：页面是缓存的，否则未保存的改动会在你
    /// 下次回来时仍留在控件里。
    /// </summary>
    public void ReloadFromSettings()
    {
        SelectTag(TargetCombo, string.IsNullOrEmpty(_settings.TargetLanguage) ? "auto" : _settings.TargetLanguage);
        // 用 Tag 而不是索引：加 Lite 后索引会变，选择与落盘必须读同一个值。
        SelectTag(ModelCombo, string.IsNullOrWhiteSpace(_settings.Model) ? "qwen-mt-flash" : _settings.Model);
        ThemeCombo.SelectedIndex = _settings.Theme?.Trim().ToLowerInvariant() switch
        {
            ThemePalette.LightSetting => 1,
            ThemePalette.DarkSetting => 2,
            _ => 0,
        };
        _accent = ThemePalette.ResolveAccent(_settings.Accent);
        UpdateAccentChips();
        HotkeyBox.Text = _settings.Hotkey;
        ClipboardFallbackCheck.IsChecked = _settings.ClipboardFallbackEnabled;
        WriteBackCheck.IsChecked = _settings.WriteBackEnabled;
        BaseUrlBox.Text = _settings.OverrideBaseUrl;
        AccountUrlBox.Text = _settings.AccountBaseUrl;
        AutoStartCheck.IsChecked = StartupRegistration.IsEnabled();
        // 低内存模式只决定下次启动用哪种 WPF 渲染，不影响当前进程
        SoftwareRenderingCheck.IsChecked = _settings.SoftwareRendering;
        // Key 在磁盘上由 DPAPI 加密，且从不回显：输入框为空表示「别动它」
        KeyBox.Clear();
        HotkeyError.Visibility = Visibility.Collapsed;
        ExclusionHint.Visibility = Visibility.Collapsed;
        RefreshExcludedList();
    }

    private static string AboutLine()
    {
        var asm = typeof(SettingsPage).Assembly;
        var version = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = asm.GetName().Version?.ToString() ?? "1.0.0";
        // 1.0.0+<40 位十六进制> 告诉不了用户任何东西；只保留短的 commit id
        var plus = version.IndexOf('+');
        if (plus > 0 && version.Length - plus > 8)
            version = version[..(plus + 8)];
        var build = asm.Location is { Length: > 0 } location
            ? System.IO.File.GetLastWriteTime(location).ToString("yyyy-MM-dd")
            : "unknown";
        return $"Fast Translation {version}\n构建日期 {build}\n未签名测试版 — 请仅从可信来源获取安装包";
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // 检查是异步的，而且可能弹出它自己的对话框；本页保持可用
        UpdateHint.Text = "正在检查更新…";
        _checkUpdates?.Invoke();
    }

    private void RefreshExcludedList()
    {
        ExcludedList.Items.Clear();
        foreach (var name in _settings.ExcludedProcesses.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ExcludedList.Items.Add(name);
        }
        // 有边框的空列表看起来像控件坏了；要说清它为什么是空的
        ExclusionEmpty.Visibility = ExcludedList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 每个文本框和排除应用列表都在页面的那个 ScrollViewer 里带着自己的 ScrollViewer。
    // 嵌套的那个即使已经滚到底也会把滚轮标记为已处理，于是指针停在输入框上时页面就
    // 卡死了——也就是「卡住一会儿、之后又恢复」的那份反馈。所以在页面层隧道滚轮，
    // 嵌套滚动器在该方向用不了时就把事件转给外层。
    private void Body_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject);
        if (inner is not null && CanScroll(inner, e.Delta)) return; // 交给嵌套滚动器处理

        var lines = SystemParameters.WheelScrollLines;
        var step = lines > 0 ? lines * 16.0 : lines == -1 ? Body.ViewportHeight * 0.9 : 0.0;
        if (step <= 0) return;

        e.Handled = true;
        var target = Body.VerticalOffset - Math.Sign(e.Delta) * step;
        Body.ScrollToVerticalOffset(Math.Clamp(target, 0, Body.ScrollableHeight));
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0.5 : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;

    private ScrollViewer? FindInnerScrollViewer(DependencyObject? node)
    {
        for (var current = node; current is not null && !ReferenceEquals(current, Body); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer viewer) return viewer;
        }
        return null;
    }

    private void FillForeground_Click(object sender, RoutedEventArgs e)
    {
        var name = ForegroundInfo.ProcessName();
        // 点按钮时本页在前台——如实提示，而不是悄悄把自己填进去
        //（用户必须切到别的窗口才能选另一个应用）
        if (string.IsNullOrEmpty(name) || string.Equals(name, "FastTranslation", StringComparison.OrdinalIgnoreCase))
        {
            ShowHint("当前前台是本程序,请切到目标应用后再点「填入前台应用」,或直接输入进程名。");
            return;
        }
        ExclusionInput.Text = name;
        ShowHint(string.Empty);
    }

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        var name = ExclusionInput.Text.Trim();
        if (name.Length == 0) { ShowHint("请输入进程名,例如 chrome。"); return; }
        if (_settings.ExcludedProcesses.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowHint($"「{name}」已在列表里。");
            return;
        }
        _settings.ExcludedProcesses.Add(name);
        ExclusionInput.Text = string.Empty;
        RefreshExcludedList();
        ShowHint($"已加入「{name}」,点保存生效。");
    }

    private void ShowHint(string message)
    {
        ExclusionHint.Text = message;
        ExclusionHint.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is not string name) return;
        _settings.ExcludedProcesses.RemoveAll(
            n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        RefreshExcludedList();
        ExclusionHint.Visibility = Visibility.Collapsed;
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 没有额外处理；到「保存」时才落盘
    }

    private void SelectTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (string)item.Tag == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.TargetLanguage = (TargetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        // Tag 是设置里的真实模型名；不能用 SelectedIndex，Lite 插进来后索引不再固定。
        _settings.Model = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "qwen-mt-flash";
        _settings.Theme = SelectedThemeSetting;
        _settings.Accent = ThemePalette.AccentSetting(_accent);

        var newHotkey = HotkeyBox.Text.Trim();
        if (!string.Equals(newHotkey, _settings.Hotkey, System.StringComparison.OrdinalIgnoreCase))
        {
            if (_hotkey.Register(newHotkey)) _settings.Hotkey = newHotkey;
            else
            {
                // 内联的非模态错误（在这里用 MessageBox 会阻塞整个应用的托盘流程）
                HotkeyBox.Text = _settings.Hotkey;
                HotkeyError.Text = "热键注册失败(可能被其他程序占用),已保留原热键。";
                HotkeyError.Visibility = Visibility.Visible;
                return;
            }
        }
        HotkeyError.Visibility = Visibility.Collapsed;

        _settings.ClipboardFallbackEnabled = ClipboardFallbackCheck.IsChecked == true;
        _settings.WriteBackEnabled = WriteBackCheck.IsChecked == true;
        _settings.SoftwareRendering = SoftwareRenderingCheck.IsChecked == true;
        _settings.OverrideBaseUrl = BaseUrlBox.Text.Trim();
        _settings.AccountBaseUrl = AccountUrlBox.Text.Trim();

        var key = new string(KeyBox.Password.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (key.Length > 0)
        {
            _secrets.SaveSecret(Providers.QwenMt.QwenMtProvider.ApiKeySecretName, key);
            KeyBox.Clear();
        }

        // 开机自启是注册表状态，不是设置状态——要真正应用它并汇报失败，
        // 而不是假装复选框已经生效
        var wantAutoStart = AutoStartCheck.IsChecked == true;
        if (StartupRegistration.IsEnabled() != wantAutoStart && !StartupRegistration.SetEnabled(wantAutoStart))
        {
            HotkeyError.Text = "开机启动设置失败(注册表不可写),其他设置已保存。";
            HotkeyError.Visibility = Visibility.Visible;
            _settings.FirstRunCompleted = true;
            _service.Save(_settings);
            return;
        }

        _settings.FirstRunCompleted = true;
        _service.Save(_settings);
        Closed?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ReloadFromSettings(); // 丢弃未保存的改动：页面不像窗口那样会被重建
        Closed?.Invoke();
    }

    /// <summary>下拉框当前选项对应的设置值；预览与保存都读它，避免两处 switch 走岔。</summary>
    private string SelectedThemeSetting => ThemeCombo.SelectedIndex switch
    {
        1 => ThemePalette.LightSetting,
        2 => ThemePalette.DarkSetting,
        _ => ThemePalette.SystemSetting,
    };

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 构造与 ReloadFromSettings 里设 SelectedIndex 也会走到这里，那时还没上树，别预览
        if (IsLoaded) PreviewAppearance();
    }

    private void AccentChip_Click(object sender, RoutedEventArgs e)
    {
        _accent = ThemePalette.ResolveAccent((sender as FrameworkElement)?.Tag as string);
        PreviewAppearance();
    }

    /// <summary>
    /// 把「当前控件里的选择」直接铺到整个界面上，让用户不用重启就能看到效果（v84）。
    /// 只改运行时画刷与标题栏，不落盘；落盘仍然只发生在保存按钮里。
    /// </summary>
    private void PreviewAppearance()
    {
        App.ApplyAppearance(SelectedThemeSetting, ThemePalette.AccentSetting(_accent));
        // 色块显示的是「该色在当前主题下」的品牌色，换主题后必须重取值
        UpdateAccentChips();
    }

    /// <summary>退回 settings.json 里那套外观：取消、切走页面、以及保存后离开都走这里。</summary>
    private void RevertAppearance()
        => App.ApplyAppearance(_settings.Theme, _settings.Accent);

    /// <summary>
    /// 三枚色块永远显示该强调色在**当前**主题下的品牌色（于是深色下看到的是深色那一支），
    /// 选中标记用正在运行的品牌色，未选中的用发丝线边框。
    /// </summary>
    private void UpdateAccentChips()
    {
        var chips = new (AccentTheme Accent, Button Chip, Border Dot)[]
        {
            (AccentTheme.Electro, AccentElectroChip, AccentElectroDot),
            (AccentTheme.Lake, AccentLakeChip, AccentLakeDot),
            (AccentTheme.Violet, AccentVioletChip, AccentVioletDot),
        };

        foreach (var (accent, chip, dot) in chips)
        {
            dot.Background = Swatch(ThemePalette.BrandOf(ThemePalette.Current, accent));
            var selected = accent == _accent;
            chip.BorderBrush = (Brush)FindResource(selected ? "BrandBrush" : "BorderStrongBrush");
            chip.Background = (Brush)FindResource(selected ? "BrandFillBrush" : "SurfaceBrush");
            chip.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static SolidColorBrush Swatch(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
