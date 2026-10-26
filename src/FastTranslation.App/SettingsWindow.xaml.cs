using System.Windows;
using System.Windows.Controls;
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
        HotkeyBox.Text = _settings.Hotkey;
        ClipboardFallbackCheck.IsChecked = _settings.ClipboardFallbackEnabled;
        WriteBackCheck.IsChecked = _settings.WriteBackEnabled;
        BaseUrlBox.Text = _settings.OverrideBaseUrl;
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

        _settings.FirstRunCompleted = true;
        _service.Save(_settings);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
