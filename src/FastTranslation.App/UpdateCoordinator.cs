using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 定时执行静默更新检查，外加从托盘/设置菜单进来的唯一手动入口。
///
/// 静默检查从不出声：断网、404、镜像有问题、结果已是最新——这些在用户眼里完全一样，
/// 因为一个会汇报自己失败的托盘应用只会变成噪音。
/// 只有「存在新版本」和用户显式的手动请求才会产生 UI；而手动请求是用对话框呈现的，
/// 因为托盘气泡会被 Windows 11 的通知设置吞掉，
/// 用户随后就分不清一次检查是在正常工作，还是已经坏掉了。
/// </summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly Dispatcher _dispatcher;
    private readonly Action<UpdateManifest, ReleaseVersion> _present;
    private readonly Action<UpdateCheckNotice> _show;
    private readonly Action _shutdown;
    private readonly HttpClient _http;
    private readonly UpdateChecker _checker;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _disposed;

    public UpdateCoordinator(
        Dispatcher dispatcher,
        Action<UpdateManifest, ReleaseVersion> present,
        Action<UpdateCheckNotice> show,
        Action shutdown)
    {
        _dispatcher = dispatcher;
        _present = present;
        _show = show;
        _shutdown = shutdown;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FastTranslation-Updater");
        _checker = new UpdateChecker(_http, UpdateChannel.ManifestUrls, CurrentVersion);
    }

    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var info = typeof(UpdateCoordinator).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // 1.1.0+<sha> —— 构建元数据不属于版本号的一部分
            return ReleaseVersion.TryParse(info, out var version) ? version : ReleaseVersion.None;
        }
    }

    /// <summary>
    /// 安装程序管着的副本（用户在向导里挑的目录）可以原地替换自己；解压出来的便携 zip
    /// 不行，会降级成「打开下载页」。
    /// </summary>
    public static bool IsInstalledCopy() =>
        InstallLayout.CanSelfUpdate(
            InstallDirectory(), InstallLayout.ReadRegisteredDirectory(), InstallLayout.ProtectedRoots());

    /// <summary>本次构建运行的目录；路径无法解析时为 null。</summary>
    public static string? InstallDirectory() =>
        Environment.ProcessPath is { Length: > 0 } exe ? Path.GetDirectoryName(Path.GetFullPath(exe)) : null;

    public void Start()
    {
        // 托盘应用一天要启动几十次；首次检查刻意往后排，这样它不会和冷启动阶段的
        // 工作（正是这些工作让应用变得可用）抢时间
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer!.Interval = CheckInterval;
            await CheckAsync(interactive: false).ConfigureAwait(true);
        };
        _timer.Start();
    }

    public async Task CheckAsync(bool interactive)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var current = CurrentVersion;
            var result = await _checker.CheckAsync(CancellationToken.None).ConfigureAwait(true);
            switch (result.Status)
            {
                case UpdateCheckStatus.Available when result.Manifest is { } manifest:
                    UpdateLog.Write($"check ok via {result.SourceUrl}");
                    UpdateLog.Write($"update available: {manifest.Version} (running {current})");
                    _present(manifest, current);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateLog.Write($"check ok via {result.SourceUrl}");
                    UpdateLog.Write($"up to date ({current})");
                    if (interactive) _show(UpdateCheckNotice.UpToDate(current));
                    break;
                default:
                    UpdateLog.Write($"check failed: {result.Detail}");
                    if (interactive) _show(UpdateCheckNotice.Failed(UpdateChannel.ReleasesPageUrl));
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"check threw: {ex}");
            if (interactive) _show(UpdateCheckNotice.Failed(UpdateChannel.ReleasesPageUrl));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 运行安装程序并退出。安装程序会等本进程结束（它拿到了我们的 PID），装完后自己
    /// 启动新版本，所以应用不能再自己回来。
    /// </summary>
    public bool ApplyUpdate(string setupPath, out string error)
    {
        if (!UpdateInstaller.LaunchInstaller(setupPath, Environment.ProcessId, InstallDirectory(), out error))
        {
            UpdateLog.Write($"installer launch failed: {error}");
            return false;
        }
        UpdateLog.Write($"installer launched: {setupPath}");
        _shutdown();
        return true;
    }

    internal HttpClient Http => _http;

    public void Dispose()
    {
        _disposed = true;
        _timer?.Stop();
        _timer = null;
        _http.Dispose();
    }
}
