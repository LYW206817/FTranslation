using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class UpdateInstallerTests
{
    private const string GitHubSetupUrl =
        "https://github.com/LYW206817/FTranslation-Updates/releases/download/v1.1.1/FastTranslation-1.1.1-Setup.exe";

    [Fact]
    public void MirrorCandidates_LeavesGarbageAlone()
    {
        var candidates = UpdateInstaller.MirrorCandidates("not a url");
        Assert.Equal(new[] { "not a url" }, candidates);
    }

    [Fact]
    public void MirrorCandidates_DoesNotProxyANonGitHubUrl()
    {
        // gh 代理只能抓取 GitHub 上的内容;给其他地址加前缀只会保证
        // 下载失败,而直连 URL 本来可以成功
        var candidates = UpdateInstaller.MirrorCandidates("https://example.com/x.exe");
        Assert.Equal(new[] { "https://example.com/x.exe" }, candidates);
    }

    [Fact]
    public void MirrorCandidates_PutsEveryProxyBeforeTheDirectHost()
    {
        var candidates = UpdateInstaller.MirrorCandidates(GitHubSetupUrl);

        var direct = candidates.ToList().IndexOf(GitHubSetupUrl);
        Assert.True(direct > 0, "the direct URL must not be the first candidate");
        Assert.Equal("https://gh-proxy.com/" + GitHubSetupUrl, candidates[0]);
        Assert.All(candidates.Take(direct), c => Assert.Contains("://gh", c));
    }

    [Fact]
    public void MirrorCandidates_HasNoDeadProxyPrefix()
    {
        // 实测已死:连接从未建立,5 秒后放弃,所以保留它只会
        // 在轮到直连主机之前先耗掉这个超时
        var candidates = UpdateInstaller.MirrorCandidates(GitHubSetupUrl);
        Assert.DoesNotContain(candidates, c => c.Contains("ghfast.top", StringComparison.OrdinalIgnoreCase));
        Assert.Single(candidates, c => c.StartsWith("https://gh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MirrorCandidates_AddsTheWwwHostLastForGitHub()
    {
        // github.com 与 www.github.com 在某些网络上重置方式不同,所以 www 被保留为
        // 最后手段——排在直连 URL 之后,而不是代理之前
        var candidates = UpdateInstaller.MirrorCandidates(GitHubSetupUrl);
        Assert.Equal("https://www.github.com/LYW206817/FTranslation-Updates/releases/download/v1.1.1/FastTranslation-1.1.1-Setup.exe",
            candidates[^1]);
    }

    [Fact]
    public void MirrorCandidates_DoesNotRewriteAnApiGitHubHost()
    {
        // api.github.com 返回的是元数据,从不是资产本身,所以任何代理都不适用于它
        const string api = "https://api.github.com/repos/LYW206817/FTranslation-Updates/releases/latest";
        Assert.Equal(new[] { api }, UpdateInstaller.MirrorCandidates(api));
    }

    [Fact]
    public void VerifyDownload_AcceptsMatchingSizeAndHash()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.True(UpdateInstaller.VerifyDownload(path, hash, 7, out var reason), reason);
            Assert.Empty(reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_RejectsWrongSizeBeforeHashing()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.False(UpdateInstaller.VerifyDownload(path, hash, 8, out var reason));
            Assert.Contains("size", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_RejectsWrongHash()
    {
        var path = WriteTemp("payload", out _);
        try
        {
            Assert.False(UpdateInstaller.VerifyDownload(path, new string('0', 64), 7, out var reason));
            Assert.Contains("sha256", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_ReportsAMissingFile()
    {
        Assert.False(UpdateInstaller.VerifyDownload(
            Path.Combine(Path.GetTempPath(), "ft-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            new string('0', 64), 1, out var reason));
        Assert.Contains("missing", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sha256OfFile_IsLowercaseHex()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.Equal(hash, UpdateInstaller.Sha256OfFile(path));
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8388608L)]   // 恰好等于阈值
    [InlineData(9000000L)]
    [InlineData(46696599L)]  // 实际发布的 1.1.2 安装包
    [InlineData(100000003L)]
    public void PlanSegments_IsContiguousAndCoversTheWholeFile(long totalLength)
    {
        var segments = UpdateInstaller.PlanSegments(totalLength);

        long expectedStart = 0;
        foreach (var (start, length) in segments)
        {
            Assert.Equal(expectedStart, start);
            Assert.True(length > 0, "an empty segment would spin forever");
            expectedStart += length;
        }
        Assert.Equal(totalLength, expectedStart);
    }

    [Fact]
    public void PlanSegments_DropsNoBytesWhenTheSizeIsNotDivisible()
    {
        // 46696599 对任何可能选用的分段大小都近乎质数,所以余数是真的
        const long total = 46696599;
        var segments = UpdateInstaller.PlanSegments(total);

        Assert.Equal(total, segments.Sum(s => s.Length));
        Assert.Equal(total, segments[^1].Start + segments[^1].Length);
    }

    [Fact]
    public void PlanSegments_KeepsASmallFileInOneSegment()
    {
        // 低于阈值时,单连接比多出的往返更快,调用方以
        // 单个分段作为走顺序路径的信号
        Assert.Equal(new[] { (0L, 4L * 1024 * 1024) }, UpdateInstaller.PlanSegments(4L * 1024 * 1024));
        Assert.Single(UpdateInstaller.PlanSegments(8L * 1024 * 1024 - 1));
        Assert.Single(UpdateInstaller.PlanSegments(1024));
        Assert.Empty(UpdateInstaller.PlanSegments(0));
        Assert.Empty(UpdateInstaller.PlanSegments(-1));
    }

    [Fact]
    public void PlanSegments_CapsTheSegmentCountForAHugeFile()
    {
        // 200 GiB 按每段 2 MiB 会是 102400 个 socket;上限正是为了不让链路
        // 被淹没、也不让应用开出一堆句柄
        var segments = UpdateInstaller.PlanSegments(200L * 1024 * 1024 * 1024);
        Assert.Equal(32, segments.Count);
        Assert.Equal(200L * 1024 * 1024 * 1024, segments.Sum(s => s.Length));
    }

    [Fact]
    public void PlanSegments_KeepsTheRealInstallerWithinTheConcurrencyCap()
    {
        var segments = UpdateInstaller.PlanSegments(46696599);
        Assert.InRange(segments.Count, 8, 32);
    }

    [Fact]
    public async Task DownloadAsync_CancelledMidSegment_LeavesNoPartFile()
    {
        const long total = 9000000;
        const string url = "https://example.com/FastTranslation-9.9.9-Setup.exe";
        var manifestJson =
            "{\"version\":\"9.9.9\",\"url\":\"" + url + "\",\"sha256\":\"" + new string('0', 64) + "\",\"size\":" + total + "}";
        Assert.True(UpdateManifest.TryParse(manifestJson, out var manifest, out var parseError), parseError);

        var partPath = Path.Combine(UpdateInstaller.UpdatesDirectory, "FastTranslation-9.9.9-Setup.exe.part");
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var http = new HttpClient(new RangedStallHandler(total)) { Timeout = TimeSpan.FromSeconds(30) };

            var download = UpdateInstaller.DownloadAsync(http, manifest!, null, cancellation.Token);
            await Task.Delay(300);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.False(File.Exists(partPath), "a cancelled download must not leave a .part file behind");
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    /// <summary>对每个带 Range 的 GET 都回 206,以便走分段路径,然后每次滴一个 KB
    /// 并永远停住——足以越过探测、进入并行读取。</summary>
    private sealed class RangedStallHandler(long totalLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = range?.From ?? 0;
            var to = range?.To ?? totalLength - 1;
            var length = to - from + 1;

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new StallingStream(length)),
            };
            response.Content.Headers.ContentLength = length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, totalLength);
            return Task.FromResult(response);
        }
    }

    private sealed class StallingStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < 1024)
            {
                var chunk = (int)Math.Min(buffer.Length, 1024 - _position);
                buffer.Span[..chunk].Fill(7);
                _position += chunk;
                return chunk;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string WriteTemp(string content, out string hash)
    {
        var path = Path.Combine(Path.GetTempPath(), "ft-update-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, content);
        hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return path;
    }
}