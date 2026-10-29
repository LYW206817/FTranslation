namespace FastTranslation.Core;

public enum UpdateCheckStatus
{
    /// <summary>有新版本发布且清单有效。</summary>
    Available,

    /// <summary>通道有应答，且已发布版本不比我们新。</summary>
    UpToDate,

    /// <summary>没有任何源返回有效清单；调用方保持静默。</summary>
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
/// 主动检查更新时对用户说的话。文案放在这里，因为只靠托盘气泡上报的检查与压根没跑过的
/// 检查无法区分；Core 保留文本，好让测试能固定它，而 App 只决定怎么展示。
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
/// 更新器去哪里找清单。项目仓库是私有的，所以清单放在另一个公开仓库，并从两个端点并行
/// 竞速读取：GitHub Pages（权威）与 gh-proxy 走 github.com raw 路由。两者都在大陆网络实测过。
/// 那边访问不到 raw.githubusercontent.com（连接被重置），而 jsDelivr 镜像因为缓存 @main
/// 返回了几个月前的清单——过期清单比死链更糟：它会把「有更新」永久变成
/// 「你已是最新」，而且再也回不来。
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

    /// <summary>面向人的下载页，供无法自安装的便携版使用。</summary>
    public static string ReleasesPageUrl => "https://github.com/" + Owner + "/" + Repository + "/releases/latest";
}

/// <summary>
/// 读取更新清单：同时向所有通道 URL 发问，拿到第一个可用答案就停。
/// 网络或格式问题从不抛异常：按设计，检查失败保持静默。
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>
    /// 「已是最新」的回答会在这段时间内保持开放，让更慢的源还能推翻它：先应答的过期镜像
    /// 不能成为「有没有更新」的最终结论。
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
                // 调用方取消了，就必须让它看到取消，而不是检查失败
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

            // 相同或更旧：记下目前见过的最高版本，并给其他源一点时间
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

    /// <summary>下一个完成的源；没有剩余源或宽限窗口已过时返回 null。</summary>
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

        // 刻意不绑定 token：调用方取消会经由在途请求自然展开，而一个被丢弃的可取消计时器
        // 会留下没人观察的异常
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
            // HttpClient.Timeout 触发：没人取消，说明这个源确实挂住了
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
