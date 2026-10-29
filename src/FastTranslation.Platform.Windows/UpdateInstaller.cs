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
/// 更新器所有会碰到这台机器的动作都在这里：镜像回退与校验后下载。
///
/// 应用更新不在这里做。应用把下载好的 Inno Setup 安装包以分离方式启动，
/// 然后立刻退出；文件替换由安装器负责，它再通过自己的 [Run] 条目
/// 重新启动新构建。反正也没法在运行中的进程内部替换 exe，
/// 单独写一个应用进程只会多出一样需要保活的东西，而安装器要改写的
/// 恰恰就是启动它的那些文件。
/// </summary>
public static class UpdateInstaller
{
    private const string GitHubHost = "github.com";

    /// <summary>
    /// github.com release 下载用的代理前缀，按顺序尝试。ghfast.top 实测
    /// 已死(连接从未建立，5 秒超时)，因此移除：一个连不通的候选
    /// 只会让用户多等一次超时，之后才轮到直连主机。
    /// </summary>
    private static readonly string[] ProxyPrefixes =
    {
        "https://gh-proxy.com/",
    };

    // 8 MiB：低于这个值时，每段额外的 TLS+请求往返开销超过
    // 并发带来的收益，而这里唯一会下载的文件就是约 46 MB 的安装包。
    private const long SegmentThresholdBytes = 8L * 1024 * 1024;

    // 在目标链路上实测：32 个套接字跑到 1.09 MB/s，单连接只有 22-35 KB/s；64 个
    // 是 1.65 MB/s——1.5 倍的收益不值得为共享的单客户端限流再多开 32 个套接字。
    private const int MaxSegments = 32;

    // 1 MiB 能让 46.7 MB 的安装包用满 32 个连接，而单个被限速的套接字
    // 在收尾时最多只会拖住这么多字节。
    private const long SegmentSizeBytes = 1L * 1024 * 1024;

    // 每段最多尝试 4 次，每次从上一次停下的字节继续，所以一次尝试不浪费
    // 字节：一条连 4 次都丢掉同一区间的链路，根本传不了这个文件。
    private const int SegmentAttempts = 4;

    // 高于这条链路单套接字 22 KB/s 的下限，又低于被限速套接字衰减后的
    // 几 KB/s：这种套接字会把整次下载卡在 97%，所以丢掉它重新请求。
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(10);
    private const long StallFloorBytes = 60L * 1024;

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation", "updates");

    /// <summary>
    /// 按尝试顺序给出下载候选。大陆网络无法完成 github.com release 的
    /// 下载(实测 objects.githubusercontent.com 只有约 9 KB/s，46 MB 要一个半小时)，
    /// 而同一个 URL 走 gh 代理能到 0.3-2 MB/s——所以对 github.com 先走代理、
    /// 直连主机作为兜底。
    ///
    /// 其他主机一律不动。这些代理只会说 GitHub 的语言：给一个非 GitHub URL
    /// 加上前缀，会把本来能成功的下载变成必然失败，所以这份列表按主机
    /// 逐个拼出来，而不是事后再改写。
    ///
    /// api.github.com 报告 release 资产用的是 assets.githubusercontent.com，
    /// 但浏览器里的 URL 永远是规范形式，所以只对 github.com 做重排序。
    /// </summary>
    internal static IReadOnlyList<string> MirrorCandidates(string url)
    {
        var candidates = new List<string> { url };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return candidates;
        if (!uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase)) return candidates;

        var ordered = new List<string>();
        foreach (var prefix in ProxyPrefixes) AddOnce(ordered, prefix + url);
        AddOnce(ordered, url);

        // 在某些网络上，github.com 与 www.github.com 的重置连接行为不同
        AddOnce(ordered, "https://www.github.com" + uri.PathAndQuery);
        return ordered;
    }

    private static void AddOnce(List<string> candidates, string candidate)
    {
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
    }

    /// <summary>
    /// 对一个该长度的文件，按顺序、首尾相接给出要并行抓取的字节区间。
    /// 长度低于阈值时只给一段，调用方正是靠这一点识别「不要分段」——
    /// 对真实文件来说，这份计划永远不会返回空列表。
    /// </summary>
    internal static IReadOnlyList<(long Start, long Length)> PlanSegments(long totalLength)
    {
        if (totalLength <= 0) return Array.Empty<(long, long)>();
        if (totalLength < SegmentThresholdBytes) return new[] { (0L, totalLength) };

        var wanted = totalLength / SegmentSizeBytes + (totalLength % SegmentSizeBytes == 0 ? 0 : 1);
        var count = (int)Math.Min(MaxSegments, wanted);
        var perSegment = totalLength / count;

        // 前 'extra' 段各多分一个字节，这样没有字节因整除而丢失
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

    /// <summary>先比大小(便宜，能抓住传输中断)，再校验哈希。</summary>
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
    /// 下载并校验安装包。先按并行字节区间尝试清单里的 URL，因为大陆到
    /// github.com 的单条 TCP 连接被限速，远低于同一链路走多个套接字
    /// 能拿到的带宽；若服务器不支持区间请求或没能收敛，再像以前那样
    /// 逐个尝试各镜像。校验失败的候选直接丢弃，不予信任——
    /// 一个会改写字节的代理，绝不能借它把可执行文件
    /// 递到用户手里。
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

        // 先试分段，且只对清单里的原始 URL：记录下来的吞吐是在直连主机上
        // 测的，重新探测代理会多出一跳没人测过的链路。
        if (await TrySegmentedAsync(http, manifest, partPath, finalPath, progress, failures, cancellationToken)
                .ConfigureAwait(false))
        {
            progress?.Report(1);
            return new DownloadResult(true, finalPath, $"{manifest.DownloadUrl} (segmented)");
        }

        // 对 github.com，MirrorCandidates 已经会先返回代理、再返回直连主机，
        // 其他主机则不加代理。在这里再重排，只会把一个非 GitHub URL 塞进
        // 根本无法服务它的代理，白白烧掉唯一可能成功的候选。
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
    /// 用并行区间下载安装包，并在 <paramref name="finalPath"/> 留下一个
    /// 通过校验的文件。服务器或链路单纯不支持这条路时返回 false，
    /// 原因追加到 <paramref name="failures"/>，而不是抛异常，
    /// 好让调用方回退到逐个候选的串行下载。
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
            // 一次 1 字节的探测同时回答两个问题：忽略 Range 的服务器或代理
            // 在这里会回 200，而 Content-Range 里带着各段必须凑齐的总长度。
            // 这个长度也就是实测的 Content-Length，清单里的尺寸必须与它一致，
            // 这样过期的清单不会拼出一个尺寸不符的文件。
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

            // 先 SetLength 把文件定好大小，这样每段都写进一个已定尺寸的文件，
            // 文件里始终只有最终字节数——不需要追加记账，内存也有界。
            var aggregated = new ProgressAggregator(manifest.SizeBytes, progress);
            var segmentFailures = new ConcurrentBag<string>();
            // 定尺寸和写入共用一个句柄，并在回读文件前关闭：
            // 写入句柄留着不关，SHA256 校验和最后的移动都会因共享冲突失败。
            using (var handle = File.OpenHandle(
                partPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous))
            {
                // 写最后一个字节就是预分配；覆盖它的那段会重写它，
                // 所以文件里始终只有最终字节数，也不需要追加记账。
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
    /// 放弃分段路线：记下原因，清理半成品文件，并在 update.log 里
    /// 留一条痕迹——回退路线的耗时约是原来的二十倍，
    /// 更新慢得离谱的用户得能看出是哪个条件把它逼上了慢路。
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
                // 重试从停下的字节继续，所以一次套接字重置只多一个短请求，
                // 而不是整段重来
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

            // 一条活着但爬得极慢的连接远不如一条新连接：丢掉它，
            // 在新连接上从这个字节续传，而不是干等。
            if (Stopwatch.GetElapsedTime(windowStart) < StallWindow) continue;
            if (windowBytes < StallFloorBytes)
                throw new IOException(
                    $"range {from}-{to} stalled at {written} ({windowBytes} B in {StallWindow.TotalSeconds:F0} s)");

            windowStart = Stopwatch.GetTimestamp();
            windowBytes = 0;
        }
        return written;
    }

    /// <summary>把各段的字节数汇总成一个单调递增的 0..1 进度。</summary>
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
    /// 以分离方式启动安装器并立即返回；调用方必须紧接着退出。
    /// /SILENT 保留进度窗口(用户得看见有事在发生)，
    /// /SUPPRESSMSGBOXES 让它无需交互。我们自己的 PID 通过自定义的
    /// /UPDATEPID= 开关带过去，安装器就能等本进程真正消失后再替换
    /// exe——托盘应用会取消普通的 WM_CLOSE，Restart Manager 清不掉它。
    /// 在这里关闭应用也不行：调用方不能阻塞。
    /// /DIR 重复指定副本运行的目录，这样静默运行不会漂到默认位置：
    /// 这种模式下，用户选目录的那个向导页根本不会出现。
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
/// 更新器自己的痕迹文件。这里的诊断很重要：一次静默失败的更新
/// 会把用户留在旧版本上，而没有任何可见症状。
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