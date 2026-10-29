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
/// 首页：一张纸、一个翻译按钮、一份结果。它就是 v47 主窗口的内容搬到页面里——侧边栏和
/// 账号区块留在窗口中。
/// </summary>
public partial class MainPage : UserControl
{
    private readonly TranslationService _translation;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly AccountCoordinator _account;
    private CancellationTokenSource? _translateCts;
    private string? _lastSource;      // 产出当前结果的那一对原文，按原样保存
    private string? _lastTarget;
    private bool _saving;

    /// <summary>「配置 Key」需要设置页，但页面不能自己切换页面。</summary>
    public event Action? SettingsRequested;

    /// <summary>「截图翻译」跑的是和托盘同一套取图流程；由宿主转发。</summary>
    public event Action? ScreenshotRequested;

    /// <summary>保存撞上了订阅闸门；宿主知道怎么去套餐页。</summary>
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
            // kind 传 null 让服务端的 NoteClassifier 判断——和工具栏走同一条路径
            var saved = await _account.SaveNoteAsync(
                kind: null,
                sourceText: _lastSource,
                targetText: _lastTarget,
                sourceLang: string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "auto" : settings.SourceLanguage,
                targetLang: string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "auto" : settings.TargetLanguage,
                model: settings.Model,
                originApp: string.Empty,          // 来源就是本程序，记下来没有意义
                cancellationToken: CancellationToken.None);

            if (saved.Succeeded)
            {
                SaveButton.Content = "已保存到" + NoteKinds.Display(saved.Value!.Kind) + "本";
                return;                            // 保持禁用：同一对内容不重复保存
            }

            // 两道闸门都靠把读者送到能解决它的地方来应答，而不是在这里解释套餐
            //（与工具栏的保存处理一致）。
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

    /// <summary>由窗口在 Key 变更后调用，也在页面重新回到视野时调用。</summary>
    public void RefreshKeyStatus()
    {
        var hasKey = !string.IsNullOrWhiteSpace(_secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        KeyStatus.Text = hasKey ? "API Key:已配置(本机加密保存)" : "API Key:未配置";
        KeyButton.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>窗口被热键唤起时，把光标交给输入纸面。</summary>
    public void FocusInput()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }
}
