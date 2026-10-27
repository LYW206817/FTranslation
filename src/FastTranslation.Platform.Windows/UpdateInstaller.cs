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
    private const string ProxyPrefix = "https://gh-proxy.com/";

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation", "updates");

    /// <summary>
    /// Download candidates in priority order. A mainland network cannot finish a
    /// github.com release download (measured: 0 bytes in 60s), but the same URL behind
    /// gh-proxy answers at ~150 KB/s, so the proxy is a first-class fallback rather than a debug aid.
    /// </summary>
    internal static IReadOnlyList<string> MirrorCandidates(string url)
    {
        var candidates = new List<string> { url };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return candidates;

        var proxied = ProxyPrefix + url;
        if (!candidates.Contains(proxied, StringComparer.OrdinalIgnoreCase)) candidates.Add(proxied);

        // assets.githubusercontent.com is what api.github.com reports for a release asset;
        // the browser URL is always the canonical form.
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return candidates;

        if (uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            var withWww = "https://www.github.com" + uri.PathAndQuery;
            if (!candidates.Contains(withWww, StringComparer.OrdinalIgnoreCase)) candidates.Add(withWww);
        }
        return candidates;
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

        // the proxy is tried before the direct URL for github.com: on a mainland network the
        // direct host answers but then trickles at ~9 KB/s from objects.githubusercontent.com,
        // which never trips a stall timer and leaves the user staring at 1% for ten minutes
        var candidates = MirrorCandidates(manifest.DownloadUrl)
            .OrderBy(c => c.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

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
