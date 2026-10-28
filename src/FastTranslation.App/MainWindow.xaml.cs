using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App;

public partial class MainWindow : Window
{
    private readonly TranslationService _translation;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly Action _openSettings;
    private readonly Action<bool> _setCaptureEnabled;
    private readonly Func<bool> _isCaptureEnabled;
    private CancellationTokenSource? _translateCts;

    public MainWindow(
        TranslationService translation,
        Func<AppSettings> settings,
        SettingsService settingsService,
        ISecretStore secrets,
        Action openSettings,
        Action<bool> setCaptureEnabled,
        Func<bool> isCaptureEnabled)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        if (Environment.GetEnvironmentVariable("FT_UITEST") is not null)
        {
            PreviewMouseLeftButtonDown += (_, _) =>
                System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", $"{DateTime.Now:HH:mm:ss.fff} window PreviewMouseDown\n");
        }
        _translation = translation;
        _settings = settings;
        _settingsService = settingsService;
        _secrets = secrets;
        _openSettings = openSettings;
        _setCaptureEnabled = setCaptureEnabled;
        _isCaptureEnabled = isCaptureEnabled;

        TargetCombo.Items.Add(new ComboBoxItem { Content = "自动(中英互译)", Tag = "auto" });
        foreach (var lang in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
        }
        SelectTag(string.IsNullOrEmpty(_settings().TargetLanguage) ? "auto" : _settings().TargetLanguage);
        ModelLabel.Text = _settings().Model;
        HotkeyHint.Text = "划词热键 " + _settings().Hotkey;
        RefreshKeyStatus();
        CaptureToggle.IsChecked = _isCaptureEnabled();
    }

    private void SelectTag(string tag)
    {
        for (var i = 0; i < TargetCombo.Items.Count; i++)
        {
            if (TargetCombo.Items[i] is ComboBoxItem item && (string)item.Tag == tag)
            {
                TargetCombo.SelectedIndex = i;
                return;
            }
        }
        TargetCombo.SelectedIndex = 0;
    }

    private void RefreshKeyStatus()
    {
        var hasKey = !string.IsNullOrWhiteSpace(_secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        KeyStatus.Text = hasKey ? "API Key:已配置(本机加密保存)" : "API Key:未配置";
        KeyButton.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var settings = _settings();
        var tag = (TargetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is not null && tag != settings.TargetLanguage)
        {
            settings.TargetLanguage = tag;
            _settingsService.Save(settings);
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TranslateButton.IsEnabled = InputBox.Text.Trim().Length > 0;
        InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            if (TranslateButton.IsEnabled) Translate_Click(this, e);
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text;
        if (text.Trim().Length == 0) return;

        TranslateButton.IsEnabled = false;
        ResultBlock.Text = "正在翻译…";
        CopyResultButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _translateCts, cts);
        try { previous?.Cancel(); } catch { }

        try
        {
            var result = await _translation.TranslateAsync(text, _settings(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            if (result.Succeeded)
            {
                ResultBlock.Text = result.TranslatedText;
                CopyResultButton.IsEnabled = true;
            }
            else
            {
                ResultBlock.Text = result.Error?.Message ?? "翻译失败";
            }
        }
        finally
        {
            TranslateButton.IsEnabled = InputBox.Text.Trim().Length > 0;
            cts.Dispose();
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBlock.Text); } catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void KeyButton_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void CaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_setCaptureEnabled is null) return; // fires during InitializeComponent (IsChecked=True in XAML)
        if (CaptureToggle.IsChecked is { } enabled)
        {
            CaptureToggle.Content = enabled ? "取词已开启" : "取词已暂停";
            _setCaptureEnabled(enabled);
        }
    }

    public void RefreshCaptureState()
    {
        if (_isCaptureEnabled() != (CaptureToggle.IsChecked == true))
        {
            CaptureToggle.IsChecked = _isCaptureEnabled();
        }
    }
}
