using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _service;
    private readonly ISecretStore _secrets;
    private readonly HotkeyService _hotkey;

    public SettingsWindow(AppSettings settings, SettingsService service, ISecretStore secrets, HotkeyService hotkey)
    {
        InitializeComponent();
        _settings = settings;
        _service = service;
        _secrets = secrets;
        _hotkey = hotkey;

        // target language combo: auto + catalog
        TargetCombo.Items.Add(new ComboBoxItem { Content = "自动(中英互译)", Tag = "auto" });
        foreach (var lang in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
        }
        SelectTag(TargetCombo, string.IsNullOrEmpty(_settings.TargetLanguage) ? "auto" : _settings.TargetLanguage);

        ModelCombo.SelectedIndex = _settings.Model.Contains("plus") ? 1 : 0;
        ThemeCombo.SelectedIndex = _settings.Theme?.Trim().ToLowerInvariant() switch
        {
            ThemePalette.LightSetting => 1,
            ThemePalette.DarkSetting => 2,
            _ => 0,
        };
        HotkeyBox.Text = _settings.Hotkey;
        ClipboardFallbackCheck.IsChecked = _settings.ClipboardFallbackEnabled;
        WriteBackCheck.IsChecked = _settings.WriteBackEnabled;
        BaseUrlBox.Text = _settings.OverrideBaseUrl;
        RefreshExcludedList();
        AutoStartCheck.IsChecked = StartupRegistration.IsEnabled();
        AboutText.Text = AboutLine();

        // SizeToContent="Height" is NOT clamped by MaxHeight (WPF measures the content
        // unconstrained), so on a 175%-scaled 960px screen the window grew past the screen and
        // pushed 保存/取消 off the bottom. Size manually against the live work area instead.
        var work = SystemParameters.WorkArea;
        Height = Math.Min(720, Math.Max(360, work.Height - 80));

    }

    private static string AboutLine()
    {
        var asm = typeof(SettingsWindow).Assembly;
        var version = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = asm.GetName().Version?.ToString() ?? "1.0.0";
        // 1.0.0+<40 hex> tells the user nothing; keep the short commit id only
        var plus = version.IndexOf('+');
        if (plus > 0 && version.Length - plus > 8)
            version = version[..(plus + 8)];
        var build = asm.Location is { Length: > 0 } location
            ? System.IO.File.GetLastWriteTime(location).ToString("yyyy-MM-dd")
            : "unknown";
        return $"Fast Translation {version}\n构建日期 {build}\n未签名测试版 — 请仅从可信来源获取安装包";
    }

    private void RefreshExcludedList()
    {
        ExcludedList.Items.Clear();
        foreach (var name in _settings.ExcludedProcesses.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ExcludedList.Items.Add(name);
        }
        // an empty bordered list reads as a broken control; say why it is empty instead
        ExclusionEmpty.Visibility = ExcludedList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The list is a scroller nested in the page scroller: while the pointer is over it the wheel
    // is consumed by the inner one, so the page "sticks" mid-scroll. Forward the wheel to the page
    // once the list cannot move any further in that direction.
    private void ExcludedList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = FindScrollViewer(ExcludedList);
        var scrollable = inner is not null && inner.ScrollableHeight > 0.5;
        var atTop = inner is null || inner.VerticalOffset <= 0.5;
        var atBottom = inner is null || inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
        var wheelUp = e.Delta > 0;

        if (scrollable && !(wheelUp ? atTop : atBottom)) return; // the list can still move: let it

        e.Handled = true;
        var step = e.Delta / 120.0 * 48.0;
        Body.ScrollToVerticalOffset(Math.Clamp(Body.VerticalOffset - step, 0, Body.ScrollableHeight));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void FillForeground_Click(object sender, RoutedEventArgs e)
    {
        var name = ForegroundInfo.ProcessName();
        // this window is foreground while the button is clicked — say so instead of
        // silently filling our own name (the user must switch windows to pick another app)
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
        // nothing extra; saved on Save
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
        _settings.Model = ModelCombo.SelectedIndex == 1 ? "qwen-mt-plus" : "qwen-mt-flash";
        // the palette is applied once at startup (App.OnStartup) — no live re-theme by design
        _settings.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => ThemePalette.LightSetting,
            2 => ThemePalette.DarkSetting,
            _ => ThemePalette.SystemSetting,
        };

        var newHotkey = HotkeyBox.Text.Trim();
        if (!string.Equals(newHotkey, _settings.Hotkey, System.StringComparison.OrdinalIgnoreCase))
        {
            if (_hotkey.Register(newHotkey)) _settings.Hotkey = newHotkey;
            else
            {
                // inline non-modal error (a MessageBox here blocks the whole app's tray flow)
                HotkeyBox.Text = _settings.Hotkey;
                HotkeyError.Text = "热键注册失败(可能被其他程序占用),已保留原热键。";
                HotkeyError.Visibility = Visibility.Visible;
                return;
            }
        }
        HotkeyError.Visibility = Visibility.Collapsed;

        _settings.ClipboardFallbackEnabled = ClipboardFallbackCheck.IsChecked == true;
        _settings.WriteBackEnabled = WriteBackCheck.IsChecked == true;
        _settings.OverrideBaseUrl = BaseUrlBox.Text.Trim();

        var key = new string(KeyBox.Password.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (key.Length > 0)
        {
            _secrets.SaveSecret(Providers.QwenMt.QwenMtProvider.ApiKeySecretName, key);
            KeyBox.Clear();
        }

        // autostart is registry state, not settings state — apply it and report failures
        // instead of pretending the checkbox took effect
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
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
