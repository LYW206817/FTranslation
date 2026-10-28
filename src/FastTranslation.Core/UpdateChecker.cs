namespace FastTranslation.Core;

public enum UpdateCheckStatus
{
    /// <summary>A newer version is published and its manifest is valid.</summary>
    Available,

    /// <summary>The channel answered and the published version is not newer than ours.</summary>
    UpToDate,

    /// <summary>No source answered with a valid manifest; the caller stays silent.</summary>
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateManifest? Manifest,
    string Detail,
    string? SourceUrl = null)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available && Manifest is not null;
}

/// <summary>
/// What an interactive check says to the user. The wording lives here because a check reported
/// only through a tray balloon is indistinguishable from one that never ran; Core keeps the text
/// so a test can pin it while the App only decides how to show it.
/// </summary>
public readonly record struct UpdateCheckNotice(string Title, string Body, string? LinkUrl = null)
{
    public static UpdateCheckNotice UpToDate(ReleaseVersion current) =>
        new("已经是最新版本", "当前版本 " + current + " 已经是最新的,不需要更新。");

    public static UpdateCheckNotice Failed(string releasesPageUrl) =>
        new("检查更新失败",
            "连不上更新服务器,可能是网络不通。你可以到下载页手动获取最新版本。" + Environment.NewLine +
            Environment.NewLine + "失败原因已记录到 update.log。",
            releasesPageUrl);
}

/// <summary>
/// Where the updater looks for the manifest. The project repository is private, so the manifest
/// lives in a separate public repository and is read from two endpoints raced in parallel: GitHub
/// Pages (canonical) and gh-proxy over the github.com raw route. Both were measured from a mainland
/// network. raw.githubusercontent.com is unreachable there (connection reset) and the jsDelivr
/// mirror served a months-old manifest because it caches @main - a stale manifest is worse than a
/// dead one: it turns "an update exists" into "you are already current" for good.
/// </summary>
public static class UpdateChannel
{
    public const string Owner = "LYW206817";
    public const string Repository = "FTranslation-Updates";
    public const string ManifestFile = "version.json";

    public static IReadOnlyList<string> ManifestUrls { get; } = new[]
    {
        "https://" + Owner.ToLowerInvariant() + ".github.io/" + Repository + "/" + ManifestFile,
        "https://gh-proxy.com/https://github.com/" + Owner + "/" + Repository + "/raw/main/" + ManifestFile,
    };

    /// <summary>Human-facing download page, used by portable copies that cannot self-install.</summary>
    public static string ReleasesPageUrl => "https://github.com/" + Owner + "/" + Repository + "/releases/latest";
}

/// <summary>
/// Reads the update manifest, asking every channel URL at once and stopping at the first usable
/// answer. Never throws for network or format problems: a failed check is silent by design.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>
    /// An "up to date" answer is held open this long so a slower source can still contradict it:
    /// a stale mirror answering first must not be the last word on whether an update exists.
    /// </summary>
    private static readonly TimeSpan StaleConfirmWindow = TimeSpan.FromMilliseconds(1500);

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _manifestUrls;
    private readonly ReleaseVersion _currentVersion;

    public UpdateChecker(HttpClient http, IReadOnlyList<string> manifestUrls, ReleaseVersion currentVersion)
    {
        _http = http;
        _manifestUrls = manifestUrls;
        _currentVersion = currentVersion;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manifestUrls.Count == 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "no manifest source configured");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<SourceResult>>(_manifestUrls.Count);
        foreach (var url in _manifestUrls) pending.Add(FetchAsync(url, race.Token));

        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        UpdateManifest? newest = null;
        string? newestUrl = null;
        DateTime? graceEnds = null;

        while (true)
        {
            var next = await NextAsync(pending, graceEnds).ConfigureAwait(false);
            if (next is not { } result) break;

            if (result.IsCancelled)
            {
                // a cancelled caller must still see cancellation, not a failed check
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            if (result.Manifest is not { } manifest)
            {
                failures[result.Url] = result.Error;
                continue;
            }

            if (manifest.Version.IsNewerThan(_currentVersion))
            {
                race.Cancel();
                return new UpdateCheckResult(UpdateCheckStatus.Available, manifest,
                    _currentVersion + " -> " + manifest.Version, result.Url);
            }

            // equal or older: keep the highest one seen and give the other sources a moment
            if (newest is null || manifest.Version.IsNewerThan(newest.Version))
            {
                newest = manifest;
                newestUrl = result.Url;
            }
            graceEnds ??= DateTime.UtcNow + StaleConfirmWindow;
        }

        if (newest is not null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, newest,
                "latest is " + newest.Version + ", current is " + _currentVersion, newestUrl);
        }

        return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
            failures.Count == 0 ? "no manifest source answered" : JoinFailures(failures));
    }

    /// <summary>Next finished source, or null once nothing is left or the grace window has passed.</summary>
    private static async Task<SourceResult?> NextAsync(List<Task<SourceResult>> pending, DateTime? graceEnds)
    {
        if (pending.Count == 0) return null;

        if (graceEnds is not { } ends)
        {
            var first = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(first);
            return await first.ConfigureAwait(false);
        }

        var budget = ends - DateTime.UtcNow;
        if (budget <= TimeSpan.Zero) return null;

        // intentionally not tied to a token: caller cancellation unwinds through the pending
        // fetches, and an abandoned cancellable timer would leave an unobserved fault behind
        var timer = Task.Delay(budget);
        var winner = await Task.WhenAny(pending.Cast<Task>().Append(timer)).ConfigureAwait(false);
        if (winner == timer) return null;

        var finished = (Task<SourceResult>)winner;
        pending.Remove(finished);
        return await finished.ConfigureAwait(false);
    }

    private string JoinFailures(Dictionary<string, string> failures) =>
        string.Join(" | ",
            _manifestUrls.Where(failures.ContainsKey).Select(url => url + " -> " + failures[url]));

    private async Task<SourceResult> FetchAsync(string url, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SourceResult.Failed(url, "HTTP " + (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return UpdateManifest.TryParse(json, out var manifest, out var error)
                ? SourceResult.Ok(url, manifest)
                : SourceResult.Failed(url, error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return SourceResult.Cancelled(url);
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout fired: nobody cancelled, so this source really did hang
            return SourceResult.Failed(url, "timeout");
        }
        catch (Exception ex)
        {
            return SourceResult.Failed(url, ex.GetType().Name);
        }
    }

    private readonly record struct SourceResult(string Url, UpdateManifest? Manifest, string Error, bool IsCancelled)
    {
        public static SourceResult Ok(string url, UpdateManifest manifest) => new(url, manifest, string.Empty, false);

        public static SourceResult Failed(string url, string error) => new(url, null, error, false);

        public static SourceResult Cancelled(string url) => new(url, null, string.Empty, true);
    }
}
