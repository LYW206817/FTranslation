using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 更新器与用户交流的唯一场所。下载就在这个对话框里进行，这样镜像失败或变慢时能显示
/// 自己的进度，而不是躲在已关闭的窗口后面干等。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateManifest _manifest;
    private readonly bool _selfInstallable;
    private CancellationTokenSource? _download;

    /// <param name="selfInstallable">
    /// 解压出来的便携版传 false：安装程序会把第二个应用装进
    /// %LOCALAPPDATA%\Programs，而不是替换当前这一份，因此这种副本
    /// 只提供下载页面。
    /// </param>
    public UpdateWindow(UpdateManifest manifest, ReleaseVersion currentVersion, bool selfInstallable)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _manifest = manifest;
        _selfInstallable = selfInstallable;

        Headline.Text = $"发现新版本 {manifest.Version}";
        Subline.Text = selfInstallable
            ? $"当前版本 {currentVersion}。更新会关闭程序、安装新版本,然后自动重新打开。"
            : $"当前版本 {currentVersion}。便携版需要手动下载新版本。";
        NotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes) ? "本次更新没有附带说明。" : manifest.Notes;
        StatusText.Text = $"安装包 {manifest.SizeBytes / 1024 / 1024.0:F1} MB";
        if (!selfInstallable)
        {
            ApplyButton.Content = "打开下载页";
            LaterButton.Content = "关闭";
        }
        Closed += (_, _) => _download?.Cancel();
    }

    /// <summary>下载成功后校验过的安装包路径；用户放弃时为 null。</summary>
    public string? DownloadedPath { get; private set; }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_selfInstallable)
        {
            OpenDownloadPage();
            return;
        }

        ApplyButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载…";
        _download = new CancellationTokenSource();

        var progress = new Progress<double>(value =>
        {
            Progress.Value = value;
            StatusText.Text = $"正在下载… {value * 100:F0}%";
        });

        // 单独的客户端：各镜像按主机不同，卡死的 socket 不能复用
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FastTranslation-Updater");

        try
        {
            var result = await UpdateInstaller.DownloadAsync(http, _manifest, progress, _download.Token);
            if (result.Success && result.Path is { Length: > 0 } path)
            {
                UpdateLog.Write($"downloaded {_manifest.Version} -> {path} from {result.Detail}");
                DownloadedPath = path;
                DialogResult = true;
                return;
            }

            UpdateLog.Write($"download failed for {_manifest.Version}: {result.Detail}");
            StatusText.Text = "下载失败。可能是网络不通,稍后再试,或到下载页手动获取。";
            ApplyButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"download threw: {ex}");
            StatusText.Text = "下载出错,稍后再试。";
            ApplyButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenDownloadPage()
    {
        var url = string.IsNullOrWhiteSpace(_manifest.ReleasePageUrl)
            ? UpdateChannel.ReleasesPageUrl
            : _manifest.ReleasePageUrl;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"open download page failed: {ex.Message}");
            StatusText.Text = $"打不开浏览器,请手动访问 {url}";
            return;
        }
        DialogResult = false;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
