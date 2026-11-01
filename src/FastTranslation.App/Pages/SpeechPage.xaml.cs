using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App.Pages;

/// <summary>
/// 语音翻译的设置页。只负责「开始前」的配置与会话入口；字幕浮窗由 App 在组装会话时拉起。
/// 设置走 Func&lt;AppSettings&gt;（与 DocumentPage 同因）：设置页随时会改，页面不缓存快照。
/// </summary>
public sealed partial class SpeechPage : UserControl
{
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private bool _loading;

    /// <summary>「开始语音翻译」被点；会话的组装、额度预检与采集都在 App 层做。</summary>
    public event Action? SpeechStartRequested;

    public SpeechPage(Func<AppSettings> settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _loading = true;
        LoadLanguages();
        ShowSourceToggle.IsChecked = _settings().SpeechShowSource;
        MaxSentencesCombo.ItemsSource = new[] { 100, 200, 500 };
        MaxSentencesCombo.SelectedItem = _settings().SpeechMaxSentences is 100 or 200 or 500
            ? _settings().SpeechMaxSentences
            : 200;
        HotkeyBox.Text = _settings().SpeechHotkey;
        _loading = false;
    }

    /// <summary>App 回报页面状态（空闲/进行中/失败原因），加载完成后调用。</summary>
    public void SetState(string text)
    {
        Dispatcher.BeginInvoke(() => StateText.Text = text);
    }

    // 额度提示由 App 异步取数后推进来,页面自己不碰账号
    public void SetQuota(string text) => QuotaText.Text = text;

    private void LoadLanguages()
    {
        // 语音的源语言永远由识别决定，这里选的只是译成哪种；「自动」=目标语言跟语言对自动配对
        TargetCombo.Items.Add(new ComboBoxItem { Content = "自动（中英互译）", Tag = "auto" });
        foreach (var language in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = language.DisplayName, Tag = language.Code });
        }

        SelectTag(_settings().SpeechTargetLanguage);
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
    }

    private string TargetLanguage =>
        TargetCombo.SelectedItem is ComboBoxItem item && item.Tag is string code ? code : "auto";

    private void Start_Click(object sender, RoutedEventArgs e) => SpeechStartRequested?.Invoke();

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings().SpeechTargetLanguage = TargetLanguage;
        _settingsService.Save(_settings());
    }

    private void ShowSource_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings().SpeechShowSource = ShowSourceToggle.IsChecked == true;
        _settingsService.Save(_settings());
    }

    private void MaxSentences_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MaxSentencesCombo.SelectedItem is not int count) return;
        _settings().SpeechMaxSentences = count;
        _settingsService.Save(_settings());
    }

    // 失焦才校验，避免输入一半就弹冲突提示；校验用 HotkeyService.TryParse，
    // 注册（连同占用检测）在 App 的热键重注册里做，与 SettingsPage 的分工一致。
    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var text = HotkeyBox.Text.Trim();
        if (!HotkeyService.TryParse(text, out _, out _))
        {
            HotkeyBox.Text = _settings().SpeechHotkey;
            HotkeyHint.Text = "组合键无效，已还原";
            return;
        }
        if (string.Equals(text, _settings().SpeechHotkey, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyHint.Text = "";
            return;
        }
        _settings().SpeechHotkey = text;
        _settingsService.Save(_settings());
        HotkeyHint.Text = "已保存，重启程序后生效";
    }
}
