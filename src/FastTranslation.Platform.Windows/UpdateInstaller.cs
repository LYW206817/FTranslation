using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// Everything the updater does that touches the machine: mirror fallback and verified download.
///
/// Applying the update is NOT done here. The app launches the downloaded Inno Setup installer
/// detached and exits immediately; the installer owns the file replacement and, through its
/// [Run] entry, starts the new build again. Replacing the exe from inside the running process
/// is impossible anyway, so a separate applier process would only add a second thing to keep
/// alive while the installer rewrites the very files it was started from.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Public GitHub release downloads are routed through these when the direct host stalls.</summary>
    private const string GitHubHost = "github.com";

    /// <summary>
    /// Proxies for github.com release downloads, in the order they are tried. Both answered with
    /// real bytes from a mainland network when measured, and both were tested repeatedly: the rate
    /// swings by an order of magnitude between runs, so the second one is a genuine fallback rather
    /// than decoration. A proxy that has gone dark simply loses its turn, the direct host stays last.
    /// </summary>
    private static readonly string[] ProxyPrefixes =
    {
        "https://gh-proxy.com/",
        "https://ghfast.top/",
    };

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation", "updates");

    /// <summary>
    /// Download candidates in the order they should be tried. A mainland network cannot finish a
    /// github.com release download (measured: ~9 KB/s from objects.githubusercontent.com, an hour and
    /// a half for 46 MB), while the same URL behind a gh proxy answered at 0.3-2 MB/s — so for
    /// github.com the proxies come first and the direct hosts are the fallback.
    ///
    /// Every other host is left alone. The proxies only speak GitHub: prefixing a foreign URL with
    /// them turns a download that worked into a guaranteed miss, which is why this list is built
    /// host by host instead of being rewritten afterwards.
    ///
    /// assets.githubusercontent.com is what api.github.com reports for a release asset, but the
    /// browser URL is always the canonical form, so reordering is only ever done for github.com.
    /// </summary>
    internal static IReadOnlyList<string> MirrorCandidates(string url)
    {
        var candidates = new List<string> { url };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return candidates;
        if (!uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase)) return candidates;

        var ordered = new List<string>();
        foreach (var prefix in ProxyPrefixes) AddOnce(ordered, prefix + url);
        AddOnce(ordered, url);

        // github.com and www.github.com reset connections differently on some networks
        AddOnce(ordered, "https://www.github.com" + uri.PathAndQuery);
        return ordered;
    }

    private static void AddOnce(List<string> candidates, string candidate)
    {
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Size first (cheap, catches interrupted transfers), then the hash.</summary>
    public static bool VerifyDownload(string path, string expectedSha256, long expectedSize, out string reason)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { reason = "file missing"; return false; }
        if (expectedSize > 0 && info.Length != expectedSize)
        {
            reason = $"size {info.Length} != {expectedSize}";
            return false;
        }
        var actual = Sha256OfFile(path);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"sha256 {actual} != {expectedSha256}";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public sealed record DownloadResult(bool Success, string? Path, string Detail);

    /// <summary>
    /// Downloads and verifies the installer, trying each mirror in turn. A candidate that
    /// fails verification is discarded rather than trusted — a proxy that rewrites bytes
    /// must not be able to hand us an executable.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        HttpClient http,
        UpdateManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var failures = new List<string>();

        // MirrorCandidates already hands back proxies before the direct hosts for github.com, and
        // nothing else is proxied. Reordering here would drag a non-GitHub URL through a proxy that
        // cannot serve it and burn the only candidate that could have worked.
        var candidates = MirrorCandidates(manifest.DownloadUrl);

        foreach (var candidate in candidates)
        {
            var fileName = FileNameFor(manifest);
            var partPath = Path.Combine(UpdatesDirectory, fileName + ".part");
            var finalPath = Path.Combine(UpdatesDirectory, fileName);
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);

                using (var request = new HttpRequestMessage(HttpMethod.Get, candidate))
                using (var response = await http
                           .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        failures.Add($"{candidate} -> HTTP {(int)response.StatusCode}");
                        continue;
                    }

                    var total = response.Content.Headers.ContentLength ?? manifest.SizeBytes;
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var target = File.Create(partPath);

                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        if (total > 0) progress?.Report(Math.Clamp(received / (double)total, 0, 1));
                    }
                }

                if (!VerifyDownload(partPath, manifest.Sha256, manifest.SizeBytes, out var reason))
                {
                    failures.Add($"{candidate} -> {reason}");
                    TryDelete(partPath);
                    continue;
                }

                File.Move(partPath, finalPath, overwrite: true);
                progress?.Report(1);
                return new DownloadResult(true, finalPath, candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate} -> {ex.GetType().Name}: {ex.Message}");
                TryDelete(partPath);
            }
        }

        return new DownloadResult(false, null, string.Join(" | ", failures));
    }

    private static string FileNameFor(UpdateManifest manifest)
    {
        var name = Path.GetFileName(Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : manifest.DownloadUrl);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = $"FastTranslation-{manifest.Version}-Setup.exe";
        return name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Launches the installer detached and returns at once; the caller must exit right after.
    /// /SILENT keeps a progress window (the user must see that something is happening) and
    /// /SUPPRESSMSGBOXES keeps it unattended. Our own PID goes along in a custom /UPDATEPID=
    /// switch so the installer can wait for this process to really be gone before it replaces
    /// the exe — the tray app cancels a plain WM_CLOSE, so Restart Manager cannot clear it.
    /// Closing the app from here is not an option either: the caller must not block.
    /// </summary>
    public static bool LaunchInstaller(string setupPath, int ownProcessId, out string error)
    {
        error = string.Empty;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID={ownProcessId}",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// The updater's own trace file. Diagnostics matter here: an update that silently fails
/// leaves the user on an old version with no visible symptom.
/// </summary>
public static class UpdateLog
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation", "update.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
