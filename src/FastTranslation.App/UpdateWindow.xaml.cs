using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// The one place the updater talks to the user. Downloads inside this dialog so a failed or
/// slow mirror shows its own progress instead of hanging behind a closed window.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateManifest _manifest;
    private readonly bool _selfInstallable;
    private CancellationTokenSource? _download;

    /// <param name="selfInstallable">
    /// False for an extracted portable copy: the installer would place a second app in
    /// %LOCALAPPDATA%\Programs instead of replacing this one, so that copy only offers the
    /// download page.
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

    /// <summary>Verified installer path once the download succeeded; null when the user declined.</summary>
    public string? DownloadedPath { get; private set; }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_selfInstallable)
        {
            OpenDownloadPage();
            return;
        }

        var installable = true;
        if (!UpdateCoordinator.IsInstalledCopy())
        {
            // a moved folder would break the installer's assumption about the target directory
            installable = NoticeWindow.Confirm(this, "安装位置会变",
                $"当前程序不在默认安装位置。安装新版本会把它装到默认位置,并保留现有设置。继续吗?");
            if (!installable) { OpenDownloadPage(); return; }
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

        // own client: the mirrors differ by host, and a stalled socket must not be reused
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
