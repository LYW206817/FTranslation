using System.Windows;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App;

public partial class OnboardingWindow : Window
{
    private readonly ISecretStore _secrets;
    private readonly AppSettings _settings;
    private readonly SettingsService _service;

    public OnboardingWindow(ISecretStore secrets, AppSettings settings, SettingsService service)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _secrets = secrets;
        _settings = settings;
        _service = service;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            TestStatus.Text = "请先输入 API Key。";
            return;
        }
        TestStatus.Text = "正在测试…";
        TestButton.IsEnabled = false;
        try
        {
            using var provider = new QwenMtProvider(() => key);
            var result = await provider.TranslateAsync(
                new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"),
                System.Threading.CancellationToken.None);
            if (result.Succeeded)
            {
                _secrets.SaveSecret(QwenMtProvider.ApiKeySecretName, key);
                KeyBox.Clear();
                TestStatus.Text = $"连接成功(示例翻译:{result.TranslatedText}),Key 已保存到本机。";
            }
            else
            {
                TestStatus.Text = $"连接失败:{result.Error?.Message}";
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length > 0) _secrets.SaveSecret(QwenMtProvider.ApiKeySecretName, key);
        _settings.FirstRunCompleted = true;
        _service.Save(_settings);
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // 没有 Key 应用照样能跑；在设置好之前翻译都会失败
        Close();
    }
}
