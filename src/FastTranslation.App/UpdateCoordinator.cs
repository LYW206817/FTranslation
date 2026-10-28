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
/// Only "a newer version exists" and an explicit manual request produce UI - and a manual
/// request produces it through a dialog, because a tray balloon is swallowed by the Windows 11
/// notification settings and the user then cannot tell a working check from a broken one.
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
            // 1.1.0+<sha> - the build metadata is not part of the version
            return ReleaseVersion.TryParse(info, out var version) ? version : ReleaseVersion.None;
        }
    }

    /// <summary>
    /// A copy the installer owns (the folder the user picked in the wizard) may replace itself in
    /// place; an extracted portable zip may not and degrades to "open the download page".
    /// </summary>
    public static bool IsInstalledCopy() =>
        InstallLayout.CanSelfUpdate(
            InstallDirectory(), InstallLayout.ReadRegisteredDirectory(), InstallLayout.ProtectedRoots());

    /// <summary>The folder this build runs from, or null when the path cannot be resolved.</summary>
    public static string? InstallDirectory() =>
        Environment.ProcessPath is { Length: > 0 } exe ? Path.GetDirectoryName(Path.GetFullPath(exe)) : null;

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
    /// Runs the installer and exits. The installer waits for this process (it receives our PID)
    /// and starts the new build when it is done, so the app must not come back on its own.
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
