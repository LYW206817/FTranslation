using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FastTranslation.Core;
using Microsoft.Win32.SafeHandles;

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
    private const string GitHubHost = "github.com";

    /// <summary>
    /// Proxies for github.com release downloads, in the order they are tried. ghfast.top was
    /// measured dead (connection never established, 5 s timeout) and was removed: a candidate that
    /// cannot answer only costs the user another timeout before the direct host gets its turn.
    /// </summary>
    private static readonly string[] ProxyPrefixes =
    {
        "https://gh-proxy.com/",
    };

    // 8 MiB: below this the extra TLS+request round trips per segment cost more than the
    // concurrency buys, and the only file this ever fetches is the ~46 MB installer.
    private const long SegmentThresholdBytes = 8L * 1024 * 1024;

    // Measured on the target link: 32 sockets gave 1.09 MB/s against 22-35 KB/s on one, while 64
    // gave 1.65 MB/s — 1.5x is not worth 32 more sockets against a shared per-client limit.
    private const int MaxSegments = 32;

    // 1 MiB keeps the 46.7 MB installer at the full 32 connections, and a single throttled socket
    // strands at most this much of the file at the end of the run.
    private const long SegmentSizeBytes = 1L * 1024 * 1024;

    // Four attempts per segment, each resuming where the last one stopped, so an attempt costs no
    // bytes: a link that loses the same range four times is not carrying the file at all.
    private const int SegmentAttempts = 4;

    // Above the 22 KB/s single-socket floor this link shows, below the few KB/s a throttled socket
    // decays to: such a socket strands the whole run at 97%, so it is dropped and asked again.
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(10);
    private const long StallFloorBytes = 60L * 1024;

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

    /// <summary>
    /// Byte ranges to fetch in parallel for a file of this length, in order and end to end.
    /// A length under the threshold yields a single segment, which is how the caller recognises
    /// "do not segment this" — the plan itself never returns an empty list for a real file.
    /// </summary>
    internal static IReadOnlyList<(long Start, long Length)> PlanSegments(long totalLength)
    {
        if (totalLength <= 0) return Array.Empty<(long, long)>();
        if (totalLength < SegmentThresholdBytes) return new[] { (0L, totalLength) };

        var wanted = totalLength / SegmentSizeBytes + (totalLength % SegmentSizeBytes == 0 ? 0 : 1);
        var count = (int)Math.Min(MaxSegments, wanted);
        var perSegment = totalLength / count;

        // the first 'extra' segments carry one more byte, so no byte is lost to the division
        var extra = totalLength % count;
        var segments = new List<(long Start, long Length)>(count);
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var length = perSegment + (i < extra ? 1 : 0);
            segments.Add((start, length));
            start += length;
        }
        return segments;
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
    /// Downloads and verifies the installer. The manifest URL is tried first as parallel byte
    /// ranges, because one mainland TCP connection to github.com is throttled far below what the
    /// same link carries over many sockets; if ranging is unavailable or does not converge, the
    /// mirrors are then tried one after another exactly as before. A candidate that fails
    /// verification is discarded rather than trusted — a proxy that rewrites bytes must not be
    /// able to hand us an executable.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        HttpClient http,
        UpdateManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdatesDirectory);

        var fileName = FileNameFor(manifest);
        var partPath = Path.Combine(UpdatesDirectory, fileName + ".part");
        var finalPath = Path.Combine(UpdatesDirectory, fileName);
        var failures = new List<string>();

        // Segmented first, and only against the manifest's own URL: the recorded throughput was
        // measured on the direct host, and re-probing a proxy would add a hop nobody measured.
        if (await TrySegmentedAsync(http, manifest, partPath, finalPath, progress, failures, cancellationToken)
                .ConfigureAwait(false))
        {
            progress?.Report(1);
            return new DownloadResult(true, finalPath, $"{manifest.DownloadUrl} (segmented)");
        }

        // MirrorCandidates already hands back proxies before the direct hosts for github.com, and
        // nothing else is proxied. Reordering here would drag a non-GitHub URL through a proxy that
        // cannot serve it and burn the only candidate that could have worked.
        var candidates = MirrorCandidates(manifest.DownloadUrl);

        foreach (var candidate in candidates)
        {
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

    /// <summary>
    /// Range-downloads the installer in parallel and leaves a verified file at
    /// <paramref name="finalPath"/>. Returns false — with the reason appended to
    /// <paramref name="failures"/> — without throwing whenever the server or the link simply does
    /// not support this route, so the caller can fall back to the sequential candidates.
    /// </summary>
    private static async Task<bool> TrySegmentedAsync(
        HttpClient http,
        UpdateManifest manifest,
        string partPath,
        string finalPath,
        IProgress<double>? progress,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var plan = PlanSegments(manifest.SizeBytes);
        if (plan.Count < 2) return false;

        try
        {
            // A one-byte probe answers two questions at once: a server or proxy that ignores Range
            // replies 200 here, and Content-Range carries the length the segments must add up to.
            // That length is also the measured Content-Length the manifest size must agree with, so
            // a stale manifest cannot be spliced into a mismatched file.
            using (var probeRequest = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUrl))
            {
                probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
                using var probe = await http
                    .SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (probe.StatusCode != HttpStatusCode.PartialContent)
                    return AbandonSegmented(failures, manifest.DownloadUrl, partPath,
                        $"probe HTTP {(int)probe.StatusCode} instead of 206");

                var serverLength = probe.Content.Headers.ContentRange?.Length;
                if (serverLength != manifest.SizeBytes)
                    return AbandonSegmented(failures, manifest.DownloadUrl, partPath,
                        $"probe length {serverLength?.ToString() ?? "unknown"} != manifest {manifest.SizeBytes}");
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);

            // SetLength up front so every segment writes into an already-sized file and the file
            // only ever holds the final byte count — no append bookkeeping, bounded memory.
            var aggregated = new ProgressAggregator(manifest.SizeBytes, progress);
            var segmentFailures = new ConcurrentBag<string>();
            // One handle for both sizing and writing, and closed again before the file is read back:
            // a write handle left open makes the SHA256 pass and the final move fail on sharing.
            using (var handle = File.OpenHandle(
                partPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous))
            {
                // Writing the last byte is the preallocation; the segment covering it overwrites it, so
                // the file only ever holds the final byte count and no append bookkeeping is needed.
                await RandomAccess
                    .WriteAsync(handle, new byte[1], manifest.SizeBytes - 1, cancellationToken)
                    .ConfigureAwait(false);

                var segments = new Task[plan.Count];
                for (var i = 0; i < plan.Count; i++)
                {
                    var segment = plan[i];
                    segments[i] = Task.Run(
                        () => DownloadSegmentAsync(http, manifest.DownloadUrl, handle, segment, aggregated, segmentFailures, cancellationToken),
                        cancellationToken);
                }
                await Task.WhenAll(segments).ConfigureAwait(false);
            }

            if (!segmentFailures.IsEmpty)
                return AbandonSegmented(failures, manifest.DownloadUrl, partPath, string.Join(", ", segmentFailures));

            if (!VerifyDownload(partPath, manifest.Sha256, manifest.SizeBytes, out var reason))
                return AbandonSegmented(failures, manifest.DownloadUrl, partPath, reason);

            File.Move(partPath, finalPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            return AbandonSegmented(failures, manifest.DownloadUrl, partPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives up on the segmented route: records why, cleans the partial file up, and leaves a trace
    /// in update.log — the fallback costs roughly twenty times the download time, so a user whose
    /// update crawled needs to be able to see which condition sent it down the slow path.
    /// </summary>
    private static bool AbandonSegmented(List<string> failures, string url, string partPath, string detail)
    {
        failures.Add($"{url} -> segmented {detail}");
        UpdateLog.Write($"segmented download abandoned ({detail})");
        TryDelete(partPath);
        return false;
    }

    private static async Task DownloadSegmentAsync(
        HttpClient http,
        string url,
        SafeFileHandle handle,
        (long Start, long Length) segment,
        ProgressAggregator progress,
        ConcurrentBag<string> failures,
        CancellationToken cancellationToken)
    {
        long written = 0;
        string lastError = "no attempt made";
        for (var attempt = 0; attempt < SegmentAttempts; attempt++)
        {
            try
            {
                // a retry resumes at the byte it stopped on, so a reset socket costs one short
                // request instead of the whole segment again
                written += await ReadSegmentAsync(http, url, handle, segment, written, progress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        failures.Add($"range {segment.Start}+{segment.Length}: {lastError}");
    }

    private static async Task<long> ReadSegmentAsync(
        HttpClient http,
        string url,
        SafeFileHandle handle,
        (long Start, long Length) segment,
        long alreadyWritten,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var from = segment.Start + alreadyWritten;
        var to = segment.Start + segment.Length - 1;
        if (from > to) return 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"range {from}-{to} -> HTTP {(int)response.StatusCode}");

        var expected = to - from + 1;
        var buffer = new byte[81920];
        long written = 0;
        long windowBytes = 0;
        var windowStart = Stopwatch.GetTimestamp();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        while (written < expected)
        {
            var wanted = (int)Math.Min(buffer.Length, expected - written);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"range {from}-{to} ended at {written}");

            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), from + written, cancellationToken)
                .ConfigureAwait(false);
            written += read;
            windowBytes += read;
            progress.Advance(read);

            // A connection that is alive but has crawled is worth far less than a fresh one: drop it
            // and resume from this byte on a new connection instead of waiting it out.
            if (Stopwatch.GetElapsedTime(windowStart) < StallWindow) continue;
            if (windowBytes < StallFloorBytes)
                throw new IOException(
                    $"range {from}-{to} stalled at {written} ({windowBytes} B in {StallWindow.TotalSeconds:F0} s)");

            windowStart = Stopwatch.GetTimestamp();
            windowBytes = 0;
        }
        return written;
    }

    /// <summary>Aggregates every segment's byte count into one monotonic 0..1 report.</summary>
    private sealed class ProgressAggregator(long totalLength, IProgress<double>? progress)
    {
        private readonly object _gate = new();
        private long _received;
        private double _reported;

        public void Advance(long bytes)
        {
            var done = Interlocked.Add(ref _received, bytes);
            if (progress is null) return;

            var value = Math.Clamp(done / (double)totalLength, 0, 1);
            lock (_gate)
            {
                if (value <= _reported) return;
                _reported = value;
            }
            progress.Report(value);
        }
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
    /// /DIR repeats the folder the copy runs from, so the silent run cannot drift to the default
    /// location: the wizard page where the user picked it never appears in this mode.
    /// </summary>
    public static bool LaunchInstaller(
        string setupPath, int ownProcessId, string? installDirectory, out string error)
    {
        error = string.Empty;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = BuildInstallerArguments(ownProcessId, installDirectory),
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

    public static string BuildInstallerArguments(int ownProcessId, string? installDirectory)
    {
        var arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID={ownProcessId}";
        if (string.IsNullOrWhiteSpace(installDirectory)) return arguments;
        var trimmed = installDirectory.Trim();
        var quoted = trimmed.Contains(' ', StringComparison.Ordinal) ? $"\"{trimmed}\"" : trimmed;
        return $"{arguments} /DIR={quoted}";
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