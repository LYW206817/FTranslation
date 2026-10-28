using System.Diagnostics;
using System.Net;
using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class UpdateCheckerTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SourceA = "https://a.test/version.json";
    private const string SourceB = "https://b.test/version.json";

    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("1.1.2");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Reply> _routes;

        public StubHandler(params (string Url, HttpStatusCode Status, string Body, int DelayMs)[] routes)
        {
            _routes = routes.ToDictionary(
                route => route.Url,
                route => new Reply(route.Status, route.Body, route.DelayMs),
                StringComparer.Ordinal);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (!_routes.TryGetValue(url, out var reply))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (reply.DelayMs > 0)
            {
                await Task.Delay(reply.DelayMs, cancellationToken);
            }

            return new HttpResponseMessage(reply.Status)
            {
                Content = new StringContent(reply.Body),
            };
        }

        private readonly record struct Reply(HttpStatusCode Status, string Body, int DelayMs);
    }

    private static string Json(string version, string url, string sha, long size) => $$"""
        {"version":"{{version}}","url":"{{url}}","sha256":"{{sha}}","size":{{size}},"notes":"stub"}
        """;

    private static string ValidJson(string version) =>
        Json(version, "https://example.com/FastTranslation-Setup.exe", Sha, 53070995);

    private static async Task<UpdateCheckResult> RunAsync(
        StubHandler handler, string[] urls, ReleaseVersion current)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var checker = new UpdateChecker(http, urls, current);
        return await checker.CheckAsync(CancellationToken.None);
    }

    [Fact]
    public void Channel_RacesTheTwoMeasuredEndpoints()
    {
        Assert.Equal(
            new[]
            {
                "https://lyw206817.github.io/FTranslation-Updates/version.json",
                "https://gh-proxy.com/https://github.com/LYW206817/FTranslation-Updates/raw/main/version.json",
            },
            UpdateChannel.ManifestUrls);
    }

    [Fact]
    public async Task Check_SucceedsOnSecondSource_AndReportsWhichOneAnswered()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.InternalServerError, string.Empty, 0),
            (SourceB, HttpStatusCode.OK, ValidJson("1.2.0"), 0));

        var result = await RunAsync(handler, new[] { SourceA, SourceB }, Current);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(SourceB, result.SourceUrl);
        Assert.Equal(ReleaseVersion.Parse("1.2.0"), result.Manifest!.Version);
    }

    [Fact]
    public async Task Check_ReportsEverySourceWhenAllFail()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.InternalServerError, string.Empty, 0),
            (SourceB, HttpStatusCode.OK, "not json", 0));

        var result = await RunAsync(handler, new[] { SourceA, SourceB }, Current);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.SourceUrl);
        Assert.Contains(SourceA, result.Detail);
        Assert.Contains("HTTP 500", result.Detail);
        Assert.Contains(SourceB, result.Detail);
        Assert.Contains("manifest is not valid JSON", result.Detail);
    }

    [Fact]
    public async Task Check_PrefersNewerSource_WhenAStaleSourceAnswersFirst()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.OK, ValidJson("1.1.0"), 0),
            (SourceB, HttpStatusCode.OK, ValidJson("1.1.2"), 300));

        var result = await RunAsync(handler, new[] { SourceA, SourceB }, ReleaseVersion.Parse("1.1.0"));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(ReleaseVersion.Parse("1.1.2"), result.Manifest!.Version);
        Assert.Equal(SourceB, result.SourceUrl);
    }

    [Fact]
    public async Task Check_WaitsForTheSlowerSourceBeforeSayingUpToDate()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.OK, ValidJson("1.1.0"), 0),
            (SourceB, HttpStatusCode.OK, ValidJson("1.1.2"), 300));

        var result = await RunAsync(handler, new[] { SourceA, SourceB }, Current);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(ReleaseVersion.Parse("1.1.2"), result.Manifest!.Version);
        Assert.Equal(SourceB, result.SourceUrl);
    }

    [Fact]
    public async Task Check_ReturnsFirstNewerVersion_WithoutWaitingForSlowSources()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.OK, ValidJson("1.3.0"), 0),
            (SourceB, HttpStatusCode.OK, ValidJson("1.3.0"), 5000));

        var started = Stopwatch.StartNew();
        var result = await RunAsync(handler, new[] { SourceA, SourceB }, Current);
        started.Stop();

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(SourceA, result.SourceUrl);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "took " + started.Elapsed.TotalSeconds + "s");
    }

    [Theory]
    [InlineData("0123456789abcdef", 53070995L, "sha256 must be 64 hex characters")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", 53070995L,
        "sha256 must be 64 hex characters")]
    [InlineData(Sha, 0L, "'size' must be a positive byte count")]
    public async Task Check_StillValidatesTheManifest(string sha, long size, string expectedError)
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.OK, Json("1.3.0", "https://example.com/FastTranslation-Setup.exe", sha, size), 0));

        var result = await RunAsync(handler, new[] { SourceA }, Current);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.False(result.HasUpdate);
        Assert.Contains(expectedError, result.Detail);
    }

    [Fact]
    public async Task Check_RejectsManifestWhoseDownloadIsNotAnExe()
    {
        using var handler = new StubHandler(
            (SourceA, HttpStatusCode.OK,
                Json("1.3.0", "https://example.com/FastTranslation-Setup.zip", Sha, 53070995), 0));

        var result = await RunAsync(handler, new[] { SourceA }, Current);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("does not point at a .exe", result.Detail);
    }

    [Fact]
    public async Task Check_SingleSourceUpToDate_DoesNotStall()
    {
        using var handler = new StubHandler((SourceA, HttpStatusCode.OK, ValidJson("1.1.2"), 0));

        var started = Stopwatch.StartNew();
        var result = await RunAsync(handler, new[] { SourceA }, Current);
        started.Stop();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(SourceA, result.SourceUrl);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "took " + started.Elapsed.TotalSeconds + "s");
    }

    [Fact]
    public void Notice_UpToDate_NamesTheRunningVersion()
    {
        var notice = UpdateCheckNotice.UpToDate(Current);

        Assert.Equal("已经是最新版本", notice.Title);
        Assert.Contains("1.1.2", notice.Body);
        // an up-to-date check offers nothing to click: the dialog would show a dead button
        Assert.Null(notice.LinkUrl);
    }

    [Fact]
    public void Notice_Failed_OffersTheDownloadPageAsALink()
    {
        var notice = UpdateCheckNotice.Failed(UpdateChannel.ReleasesPageUrl);

        Assert.Equal("检查更新失败", notice.Title);
        Assert.Equal(UpdateChannel.ReleasesPageUrl, notice.LinkUrl);
        Assert.Contains("update.log", notice.Body);
        // the URL is a clickable link in the dialog, not a raw line crammed into the sentence
        Assert.DoesNotContain(UpdateChannel.ReleasesPageUrl, notice.Body);
    }
}
