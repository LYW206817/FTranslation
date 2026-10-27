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

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, string Detail)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available && Manifest is not null;
}

/// <summary>
/// Where the updater looks for the manifest. The project repository is private, so the
/// manifest lives in a separate public repository and is read through four endpoints:
/// GitHub Pages (canonical), the jsDelivr mirror and two raw proxies — on mainland networks
/// raw.githubusercontent.com is unreachable while Pages and the proxies answer. The proxies are
/// listed twice because they fail independently: when one goes dark the other still serves the
/// file, and a manifest that cannot be read means the user never hears about an update at all.
/// </summary>
public static class UpdateChannel
{
    public const string Owner = "LYW206817";
    public const string Repository = "FTranslation-Updates";
    public const string ManifestFile = "version.json";

    public static IReadOnlyList<string> ManifestUrls { get; } = new[]
    {
        $"https://{Owner.ToLowerInvariant()}.github.io/{Repository}/{ManifestFile}",
        $"https://fastly.jsdelivr.net/gh/{Owner}/{Repository}@main/{ManifestFile}",
        $"https://gh-proxy.com/https://raw.githubusercontent.com/{Owner}/{Repository}/main/{ManifestFile}",
        $"https://ghfast.top/https://raw.githubusercontent.com/{Owner}/{Repository}/main/{ManifestFile}",
    };

    /// <summary>Human-facing download page, used by portable copies that cannot self-install.</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repository}/releases/latest";
}

/// <summary>
/// Reads the update manifest, trying each channel URL until one answers with a valid manifest.
/// Never throws for network or format problems: a failed check is silent by design.
/// </summary>
public sealed class UpdateChecker
{
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
        var failures = new List<string>();

        foreach (var url in _manifestUrls)
        {
            string json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{url} -> HTTP {(int)response.StatusCode}");
                    continue;
                }
                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{url} -> {ex.GetType().Name}");
                continue;
            }

            if (!UpdateManifest.TryParse(json, out var manifest, out var error))
            {
                failures.Add($"{url} -> {error}");
                continue;
            }

            return manifest.Version.IsNewerThan(_currentVersion)
                ? new UpdateCheckResult(UpdateCheckStatus.Available, manifest,
                    $"{_currentVersion} -> {manifest.Version}")
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, manifest,
                    $"latest is {manifest.Version}, current is {_currentVersion}");
        }

        return new UpdateCheckResult(UpdateCheckStatus.Failed, null, string.Join(" | ", failures));
    }
}
