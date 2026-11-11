using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public sealed class DocumentTranslationCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ft-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<DocumentJobResult> _results = [];
    private readonly List<string> _errors = [];

    public DocumentTranslationCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Translates_a_text_file_and_reports_the_output_path()
    {
        var source = WriteText("note.txt", "第一段\n\n第二段");

        await Build().RunAsync(source, "en", null, CancellationToken.None);

        var result = Assert.Single(_results);
        Assert.Equal(2, result.TotalSegments);
        Assert.Equal(0, result.FailedSegments);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Refuses_to_start_when_the_quota_is_too_small()
    {
        var source = WriteText("quota.txt", "一段不短的正文内容");
        var calls = 0;

        // 剩余额度故意只留 1 字符：预检就该拦，不应该真的去请求上游。
        await Build(text => { calls++; return Task.FromResult(text); })
            .RunAsync(source, "en", 1, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Empty(_results);
        var error = Assert.Single(_errors);
        Assert.Contains("剩余额度", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_an_unsupported_extension()
    {
        var source = Path.Combine(_dir, "table.xlsx");
        await File.WriteAllTextAsync(source, "not really xlsx");

        await Build().RunAsync(source, "en", null, CancellationToken.None);

        Assert.Empty(_results);
        Assert.Contains(".docx", Assert.Single(_errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_file_over_the_size_limit()
    {
        var source = Path.Combine(_dir, "big.txt");
        await using (var stream = File.Create(source)) stream.SetLength(DocumentLimits.MaxFileBytes + 1);

        await Build().RunAsync(source, "en", null, CancellationToken.None);

        Assert.Empty(_results);
        Assert.Contains("MB", Assert.Single(_errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_a_new_file_and_leaves_the_source_untouched()
    {
        var source = WriteText("keep.txt", "原文第一段\n\n原文第二段");
        var before = await File.ReadAllBytesAsync(source);

        await Build().RunAsync(source, "en", null, CancellationToken.None);

        var result = Assert.Single(_results);
        Assert.NotEqual(source, result.OutputPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Reports_progress_from_zero_to_one()
    {
        var source = WriteText("progress.txt", "第一段\n\n第二段\n\n第三段");
        var updates = new List<(double Fraction, int Done, int Total)>();

        await Build(progress: (fraction, done, total) => updates.Add((fraction, done, total)))
            .RunAsync(source, "en", null, CancellationToken.None);

        Assert.Equal(0, updates[0].Done);
        Assert.Equal(1d, updates[^1].Fraction, 3);
        Assert.Equal(3, updates[^1].Total);
        Assert.All(updates, update => Assert.Equal(3, update.Total));
    }

    [Fact]
    public async Task Marks_a_failed_segment_instead_of_failing_the_job()
    {
        var source = WriteText("partial.txt", new string('甲', 100) + "\n\n" + new string('乙', 100));

        // 第一段总失败、第二段正常：作业仍要落地，只有一段带「未翻译」标记。
        var attempts = 0;
        await Build(text =>
        {
            attempts++;
            if (text.Contains('甲', StringComparison.Ordinal)) throw new InvalidOperationException("上游炸了");
            return Task.FromResult(text);
        }).RunAsync(source, "en", null, CancellationToken.None);

        var result = Assert.Single(_results);
        Assert.Equal(1, result.FailedSegments);
        Assert.Contains("【未翻译】", await File.ReadAllTextAsync(result.OutputPath), StringComparison.Ordinal);
        Assert.True(attempts > 1);
    }

    [Fact]
    public async Task Cancellation_does_not_report_an_error()
    {
        var source = WriteText("cancel.txt", new string('文', 200));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Build().RunAsync(source, "en", null, cts.Token);

        Assert.Empty(_results);
        Assert.Empty(_errors);
    }

    /// <summary>v127: 有失败段时断点必须留着，重试只补译失败段，不能重扣已译段的额度。</summary>
    [Fact]
    public async Task Keeps_the_checkpoint_when_segments_failed_so_retry_only_translates_them()
    {
        var source = WriteText("retry.txt", new string('甲', 100) + "\n\n" + new string('乙', 100));
        await Build(text => text.Contains('甲', StringComparison.Ordinal)
                ? throw new InvalidOperationException("上游炸了")
                : Task.FromResult(text))
            .RunAsync(source, "en", null, CancellationToken.None);
        Assert.Equal(1, Assert.Single(_results).FailedSegments);
        _results.Clear();

        var retried = new List<string>();
        await Build(text => { retried.Add(text); return Task.FromResult("译:" + text); })
            .RunAsync(source, "en", null, CancellationToken.None);

        Assert.Single(retried);
        Assert.Contains("甲", retried[0], StringComparison.Ordinal);
        Assert.DoesNotContain("乙", retried[0], StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(_results).FailedSegments);
    }

    /// <summary>v127: 预检只该看断点回填之后真正还要译的字数。</summary>
    [Fact]
    public async Task Quota_precheck_counts_only_the_segments_left_after_a_checkpoint()
    {
        var source = WriteText("quota-resume.txt", new string('甲', 100) + "\n\n" + new string('乙', 100));
        await Build(text => text.Contains('甲', StringComparison.Ordinal)
                ? throw new InvalidOperationException("上游炸了")
                : Task.FromResult(text))
            .RunAsync(source, "en", null, CancellationToken.None);
        Assert.Single(_results);
        _results.Clear();

        // 断点里乙已译好，只剩 100 字符要花钱；额度 100 必须放行，按整份 200 算会被误拒。
        var calls = 0;
        await Build(text => { calls++; return Task.FromResult(text); })
            .RunAsync(source, "en", 100, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(_results);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Rejects_a_paragraph_over_the_single_segment_limit()
    {
        var source = WriteText("long.txt", new string('长', DocumentLimits.MaxSegmentChars + 1));

        await Build().RunAsync(source, "en", null, CancellationToken.None);

        Assert.Empty(_results);
        Assert.Contains("单段", Assert.Single(_errors), StringComparison.Ordinal);
    }

    private DocumentTranslationCoordinator Build(
        Func<string, Task<string>>? translate = null,
        Action<double, int, int>? progress = null) =>
        new(
            format => new TextDocumentTranslator(format),
            (text, _, _) => (translate ?? (value => Task.FromResult("[en] " + value)))(text),
            () => _dir,
            _results.Add,
            _errors.Add,
            progress ?? ((_, _, _) => { }),
            _dir);

    private string WriteText(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
