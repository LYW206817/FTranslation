using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public sealed class SegmentTranslatorTests
{
    private static DocumentSegment Segment(int index, string source, string part = "/word/document.xml") =>
        new(index, part + "#p" + index, source, null, SegmentStatus.Pending);

    [Fact]
    public async Task Merges_short_consecutive_paragraphs()
    {
        var segments = new[]
        {
            Segment(0, "第一句短话"),
            Segment(1, "第二句短话"),
            Segment(2, "第三句短话"),
        };
        var calls = new List<string>();

        var result = await SegmentTranslator.TranslateAsync(segments, Echo(calls), null, CancellationToken.None);

        // 三个短段应该合成一次请求，回切成三段译文。
        Assert.Single(calls);
        Assert.All(result, segment => Assert.Equal(SegmentStatus.Done, segment.Status));
        Assert.Equal(["第一句短话", "第二句短话", "第三句短话"], result.Select(segment => segment.Target));
    }

    [Fact]
    public async Task Does_not_merge_long_paragraphs()
    {
        // 单段上限 2,000 字符，取 2,500 保证各成一个单元。
        var long1 = new string('甲', 2_500);
        var long2 = new string('乙', 2_500);
        var calls = new List<string>();

        var result = await SegmentTranslator.TranslateAsync([Segment(0, long1), Segment(1, long2)], Echo(calls), null, CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal([long1, long2], result.Select(segment => segment.Target));
    }

    [Fact]
    public async Task Does_not_merge_across_parts_or_gaps()
    {
        var calls = new List<string>();
        var segments = new[]
        {
            Segment(0, "页眉一句话", "/word/header1.xml"),
            Segment(1, "正文第一句"),
            // 中间缺了 Index 2（被跳过的段），不能合并。
            Segment(3, "正文第三句"),
        };

        await SegmentTranslator.TranslateAsync(segments, Echo(calls), null, CancellationToken.None);

        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task Retries_twice_then_degrades_on_count_mismatch()
    {
        var segments = new[]
        {
            Segment(0, "第一句短话"),
            Segment(1, "第二句短话"),
        };
        var mergedCalls = 0;
        var singleCalls = 0;

        Task<string> Translate(string text, CancellationToken _)
        {
            if (text.Contains("\n\n", StringComparison.Ordinal))
            {
                mergedCalls++;
                // 故意只回一段：段数不符，触发整包重试。
                return Task.FromResult("一段不该出现的合并译文");
            }

            singleCalls++;
            return Task.FromResult(text);
        }

        var result = await SegmentTranslator.TranslateAsync(segments, Translate, null, CancellationToken.None);

        // 3 次合并尝试（首试 + 2 次重试）后降级成 2 次逐段请求。
        Assert.Equal(3, mergedCalls);
        Assert.Equal(2, singleCalls);
        Assert.All(result, segment => Assert.Equal(SegmentStatus.Done, segment.Status));
    }

    [Fact]
    public async Task Failed_segment_is_marked_not_thrown()
    {
        var segments = new[] { Segment(0, new string('长', 2_500)) };
        var attempts = 0;

        Task<string> Translate(string _, CancellationToken __)
        {
            attempts++;
            throw new InvalidOperationException("上游 500");
        }

        var result = await SegmentTranslator.TranslateAsync(segments, Translate, null, CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(SegmentStatus.Failed, result[0].Status);
        Assert.Null(result[0].Target);
        Assert.Equal(segments[0].Source, result[0].Source);
    }

    [Fact]
    public async Task Skips_segments_that_are_already_done()
    {
        var segments = new[]
        {
            Segment(0, "已经译过的段") with { Target = "done", Status = SegmentStatus.Done },
            Segment(1, new string('新', 2_500)),
        };
        var calls = new List<string>();

        var result = await SegmentTranslator.TranslateAsync(segments, Echo(calls), null, CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal("done", result[0].Target);
    }

    [Fact]
    public async Task Reports_progress_once_per_unit()
    {
        // 用同步记录而不是 Progress<T>：Progress 的回调是异步投递的，
        // 断言就变成「等多久才算投递完」的玄学，这里要验的是「报了两次、各一次」。
        var reporter = new InlineProgress();
        // 两个单元：各 2,500 字符，超过单段 2,000 的合并上限，不会被并成一次请求。
        var segments = new[] { Segment(0, new string('甲', 2_500)), Segment(1, new string('乙', 2_500)) };

        await SegmentTranslator.TranslateAsync(segments, Echo([]), reporter, CancellationToken.None);

        Assert.Equal([1, 2], reporter.Values);
    }

    [Fact]
    public async Task Restores_placeholders_in_translated_text()
    {
        var segments = new[] { Segment(0, new string('文', 100) + " `code()`") };

        var result = await SegmentTranslator.TranslateAsync(
            segments,
            (text, _) => Task.FromResult(text.Replace("文", "x", StringComparison.Ordinal)),
            null,
            CancellationToken.None);

        Assert.Equal(SegmentStatus.Done, result[0].Status);
        Assert.Contains("`code()`", result[0].Target!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var segments = new[] { Segment(0, new string('文', 2_500)) };

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SegmentTranslator.TranslateAsync(segments, Echo([]), null, cts.Token));
    }

    [Fact]
    public async Task Quota_exhaustion_aborts_the_whole_document_without_retrying_every_segment()
    {
        // v75: 每个单元前都要按 30 RPM 排队，如果额度耗尽了还逐单元地试，
        // 658 段的文档要白等一分多钟才看到「余额不足」——这里断言它第一次就放弃。
        var segments = Enumerable.Range(0, 20).Select(i => Segment(i, new string('文', 2_500))).ToArray();
        var calls = 0;

        var failure = await Assert.ThrowsAsync<QuotaExhaustedException>(() =>
            SegmentTranslator.TranslateAsync(
                segments,
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    throw new QuotaExhaustedException("API Key 的额度或余额不足");
                },
                null,
                CancellationToken.None));

        Assert.Equal("API Key 的额度或余额不足", failure.Message);
        // 并发上限 4：最多 4 个单元已经在飞，其余的都该被联动 token 叫停。
        Assert.InRange(calls, 1, 4);
    }

    /// <summary>一个「只做回声」的假模型：合并请求原样返回，回切后每段仍是原文。</summary>
    private static Func<string, CancellationToken, Task<string>> Echo(List<string> calls) =>
        (text, _) =>
        {
            calls.Add(text);
            return Task.FromResult(text);
        };

    /// <summary>同步记录进度：<see cref="Progress{T}"/> 走调度器异步投递，测起来不稳。</summary>
    private sealed class InlineProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }
}
