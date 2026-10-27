using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// Silent update checks on a timer plus the one manual path from the tray/settings menu.
///
/// A silent check never speaks: no network, a 404, a bad mirror or an up-to-date result all
/// look identical to the user, because a tray app that reports its own failures is noise.
/// Only "a newer version exists" and an explicit manual request produce UI.
/// </summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly Dispatcher _dispatcher;
    private readonly Action<UpdateManifest, ReleaseVersion> _present;
    private readonly Action<string, string> _notify; // title, text
    private readonly Action _shutdown;
    private readonly HttpClient _http;
    private readonly UpdateChecker _checker;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _disposed;

    public UpdateCoordinator(
        Dispatcher dispatcher,
        Action<UpdateManifest, ReleaseVersion> present,
        Action<string, string> notify,
        Action shutdown)
    {
        _dispatcher = dispatcher;
        _present = present;
        _notify = notify;
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
            // 1.1.0+<sha> — the build metadata is not part of the version
            return ReleaseVersion.TryParse(info, out var version) ? version : ReleaseVersion.None;
        }
    }

    /// <summary>
    /// An extracted portable copy cannot be replaced by the installer, so it must not be told
    /// it can update itself; the dialog degrades to "open the download page" instead.
    /// </summary>
    public static bool IsInstalledCopy()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FastTranslation");
        var actual = Path.GetDirectoryName(Path.GetFullPath(exe));
        return actual is not null &&
               string.Equals(Path.TrimEndingDirectorySeparator(actual),
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)),
                   StringComparison.OrdinalIgnoreCase);
    }

    public void Start()
    {
        // a tray app starts dozens of times a day; the first check waits so it never competes
        // with the cold-start work that makes the app usable
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
                    UpdateLog.Write($"update available: {manifest.Version} (running {current})");
                    _present(manifest, current);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateLog.Write($"up to date ({current})");
                    if (interactive) _notify("Fast Translation", $"已经是最新版本 {current}。");
                    break;
                default:
                    UpdateLog.Write($"check failed: {result.Detail}");
                    if (interactive) _notify("检查更新失败", "连不上更新服务器,请稍后再试。");
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"check threw: {ex}");
            if (interactive) _notify("检查更新失败", "检查更新时出错,请稍后再试。");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Runs the installer and exits. The installer waits for this process (it receives our PID)
    /// and starts the new build when it is done, so the app must not come back on its own.
    /// </summary>
    public bool ApplyUpdate(string setupPath, out string error)
    {
        if (!UpdateInstaller.LaunchInstaller(setupPath, Environment.ProcessId, out error))
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
