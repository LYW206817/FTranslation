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
                TestStatus.Text = $"连接成功（示例翻译：{result.TranslatedText}），Key 已保存到本机。";
                TestStatus.SetResourceReference(
                    System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            else
            {
                TestStatus.Text = $"连接失败：{result.Error?.Message}";
                TestStatus.SetResourceReference(
                    System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            }
        }
        catch (Exception)
        {
            // async void 事件处理器：测试失败必须留在窗口里，不能让 UI 线程停摆。
            TestStatus.Text = "连接失败：请检查网络或 Key 后重试。";
            TestStatus.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
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
        // v143：「稍后再说」要算作已看过引导。原来不写 FirstRunCompleted，下次启动又会
        // 弹同一扇窗——用户说的「稍后」不是「每次启动都问我」。没填 Key 时翻译会失败，
        // 但设置页随时可以补。
        _settings.FirstRunCompleted = true;
        _service.Save(_settings);
        Close();
    }
}
