using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App.Pages;

/// <summary>
/// The 首页 page: one sheet of paper, a translate button, and the result. It is the v47 main
/// window's content moved onto a page — the sidebar and the account block live in the window.
/// </summary>
public partial class MainPage : UserControl
{
    private readonly TranslationService _translation;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly AccountCoordinator _account;
    private CancellationTokenSource? _translateCts;
    private string? _lastSource;      // the pair that produced the current result, saved as-is
    private string? _lastTarget;
    private bool _saving;

    /// <summary>「配置 Key」needs the settings page, but a page must not switch pages itself.</summary>
    public event Action? SettingsRequested;

    /// <summary>「截图翻译」runs the same capture flow the tray does; the owner forwards it.</summary>
    public event Action? ScreenshotRequested;

    /// <summary>Saving hit a subscription gate; the owner knows how to reach the plan page.</summary>
    public event Action? PlanRequested;

    public MainPage(
        TranslationService translation,
        AccountCoordinator account,
        Func<AppSettings> settings,
        SettingsService settingsService,
        ISecretStore secrets)
    {
        InitializeComponent();
        _translation = translation;
        _account = account;
        _settings = settings;
        _settingsService = settingsService;
        _secrets = secrets;

        TargetCombo.Items.Add(new ComboBoxItem { Content = "自动(中英互译)", Tag = "auto" });
        foreach (var lang in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
        }
        SelectTag(string.IsNullOrEmpty(_settings().TargetLanguage) ? "auto" : _settings().TargetLanguage);
        ModelLabel.Text = _settings().Model;
        HotkeyHint.Text = "划词热键 " + _settings().Hotkey;
        RefreshKeyStatus();
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
        SaveButton.IsEnabled = false;
        _lastSource = null;
        _lastTarget = null;

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
                SaveButton.IsEnabled = true;
                _lastSource = text;
                _lastTarget = result.TranslatedText;
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _lastSource is null || _lastTarget is null) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        var original = SaveButton.Content;
        SaveButton.Content = "保存中…";
        try
        {
            var settings = _settings();
            // kind: null asks the server's NoteClassifier to decide — the same path the toolbar uses
            var saved = await _account.SaveNoteAsync(
                kind: null,
                sourceText: _lastSource,
                targetText: _lastTarget,
                sourceLang: string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "auto" : settings.SourceLanguage,
                targetLang: string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "auto" : settings.TargetLanguage,
                model: settings.Model,
                originApp: string.Empty,          // the source is this program; recording it says nothing
                cancellationToken: CancellationToken.None);

            if (saved.Succeeded)
            {
                SaveButton.Content = "已保存到" + NoteKinds.Display(saved.Value!.Kind) + "本";
                return;                            // stays disabled: the same pair is not saved twice
            }

            // Both gates are answered by sending the reader somewhere that can clear them,
            // not by explaining the plan here (same handling as the toolbar's save).
            switch (saved.Error!.Code)
            {
                case AccountErrorCode.NotSignedIn:
                case AccountErrorCode.SubscriptionRequired:
                    SaveButton.Content = original;
                    SaveButton.IsEnabled = true;
                    PlanRequested?.Invoke();
                    return;
                default:
                    SaveButton.Content = "保存失败";
                    SaveButton.IsEnabled = true;
                    ResultBlock.Text = saved.Error.Display;
                    return;
            }
        }
        catch (Exception)
        {
            SaveButton.Content = "保存失败";
            SaveButton.IsEnabled = true;
        }
        finally
        {
            _saving = false;
        }
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();

    private void KeyButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>Called by the window after the key changes, and when the page comes back into view.</summary>
    public void RefreshKeyStatus()
    {
        var hasKey = !string.IsNullOrWhiteSpace(_secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        KeyStatus.Text = hasKey ? "API Key:已配置(本机加密保存)" : "API Key:未配置";
        KeyButton.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Gives the caret to the input sheet when the window is brought up by a hotkey.</summary>
    public void FocusInput()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }
}
