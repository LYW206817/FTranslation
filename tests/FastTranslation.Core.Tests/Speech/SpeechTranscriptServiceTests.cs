using FastTranslation.Core.Speech;
using Xunit;

namespace FastTranslation.Core.Tests.Speech;

public sealed class SpeechTranscriptServiceTests
{
    private static SpeechSessionOptions Options(
        int maxConcurrency = 2,
        int queueCapacity = 8,
        int debounceMs = 0) => new(16000, 200, debounceMs, maxConcurrency, queueCapacity);

    [Fact]
    public void Provisional_updates_never_reach_the_translator()
    {
        int calls = 0;
        var svc = new SpeechTranscriptService((_, _) => { calls++; return Task.FromResult("x"); }, Options());

        svc.SubmitSentence(1, "你好", final: false);
        svc.SubmitSentence(1, "你好，世界", final: false);
        svc.DrainDebounce();

        Assert.Equal(0, calls);
        var line = Assert.Single(svc.Snapshot());
        Assert.Equal("你好，世界", line.Source);
        Assert.False(line.IsFinal);
        Assert.Equal("", line.Target);

        svc.SubmitSentence(1, "你好，世界。", final: true);
        svc.DrainDebounce();
        Assert.Equal(1, calls);
        Assert.True(svc.Snapshot()[0].IsFinal);
        Assert.Equal("x", svc.Snapshot()[0].Target);
    }

    [Fact]
    public async Task Does_not_cancel_previous_sentence_when_new_final_arrives()
    {
        var gate1 = new TaskCompletionSource();
        var seen = new List<(string Text, CancellationToken Token)>();
        var svc = new SpeechTranscriptService(
            (text, ct) => { seen.Add((text, ct)); return Task.FromResult("t"); },
            Options(maxConcurrency: 1));

        svc.SubmitSentence(1, "第一句", final: true);
        svc.DrainDebounce();
        Assert.Single(seen);

        // 上一批还在途（真实实现是 await MT），新终稿不能取消它，只能排队
        svc.SubmitSentence(2, "第二句", final: true);
        svc.DrainDebounce();
        Assert.Equal(2, seen.Count);
        await WaitUntilAsync(() => svc.Snapshot()[1].Target == "t");
        Assert.All(seen, s => Assert.False(s.Token.IsCancellationRequested));
        Assert.Equal("第一句", svc.Snapshot()[0].Source);
        Assert.Equal("t", svc.Snapshot()[0].Target);
        Assert.Equal("t", svc.Snapshot()[1].Target);
        _ = gate1;
    }

    [Fact]
    public async Task Caps_concurrency_at_two()
    {
        var pending = new List<TaskCompletionSource>();
        int concurrent = 0, peak = 0;
        var svc = new SpeechTranscriptService(
            (_, _) =>
            {
                concurrent++;
                peak = Math.Max(peak, concurrent);
                var gate = new TaskCompletionSource();
                pending.Add(gate);
                return gate.Task.ContinueWith(_ =>
                {
                    concurrent--;
                    return "y";
                });
            },
            Options(maxConcurrency: 2));

        for (int i = 1; i <= 3; i++)
        {
            svc.SubmitSentence(i, $"句{i}", final: true);
            svc.DrainDebounce();
        }

        Assert.Equal(2, peak);
        Assert.Equal(2, pending.Count);

        pending[0].SetResult();
        await WaitUntilAsync(() => pending.Count >= 2);
        pending[1].SetResult();
        await WaitUntilAsync(() => pending.Count >= 3);
        pending[2].SetResult();
        await WaitUntilAsync(() => svc.Snapshot().All(l => l.Target == "y"));
        Assert.All(svc.Snapshot(), l => Assert.Equal("y", l.Target));
        Assert.Equal(3, pending.Count);
    }

    [Fact]
    public async Task Drops_oldest_batch_when_queue_is_full()
    {
        var seen = new List<string>();
        var blocked = new TaskCompletionSource();
        async Task<string> Translate(string text, CancellationToken _)
        {
            seen.Add(text);
            // 卡住第一批 A：之后 B 只能排队，C 再来时 B 被挤出（queueCapacity=1）
            if (text == "A")
            {
                await blocked.Task;
                return "A-译";
            }

            return text + "-译";
        }

        var svc = new SpeechTranscriptService(Translate, Options(maxConcurrency: 1, queueCapacity: 1));

        svc.SubmitSentence(1, "A", final: true);
        svc.DrainDebounce();
        svc.SubmitSentence(2, "B", final: true);
        svc.DrainDebounce();
        svc.SubmitSentence(3, "C", final: true);
        svc.DrainDebounce();

        Assert.Equal(1, svc.DroppedCount);
        Assert.Equal(["A"], seen);  // C 要等 A 让出并发名额才会开始
        blocked.SetResult();
        await WaitUntilAsync(() => seen.Count == 2);
        Assert.Equal(["A", "C"], seen);
        Assert.Equal("A-译", svc.Snapshot()[0].Target);
        Assert.Equal("", svc.Snapshot()[1].Target);
        Assert.Equal("C-译", svc.Snapshot()[2].Target);
    }

    [Fact]
    public async Task Backfills_merged_batch_line_by_line()
    {
        var svc = new SpeechTranscriptService((_, _) => Task.FromResult("hello\nworld"), Options());
        svc.SubmitSentence(1, "你", final: true);
        svc.SubmitSentence(2, "好", final: true);
        svc.DrainDebounce();

        await WaitUntilAsync(() => svc.Snapshot()[0].Target == "hello");
        Assert.Equal("world", svc.Snapshot()[1].Target);
    }

    [Fact]
    public async Task Backfills_whole_text_to_first_sentence_when_count_mismatches()
    {
        var svc = new SpeechTranscriptService((_, _) => Task.FromResult("一整段译文"), Options());
        svc.SubmitSentence(1, "第", final: true);
        svc.SubmitSentence(2, "二", final: true);
        svc.DrainDebounce();

        await WaitUntilAsync(() => svc.Snapshot()[0].Target == "一整段译文");
        Assert.Equal("", svc.Snapshot()[1].Target);
    }

    [Fact]
    public async Task The_debounce_timer_fires_on_its_own_after_a_final_sentence()
    {
        // v94 回归：定时器曾在构造时就起一次性倒计时，会话开始时 _pending 还空着，那唯一一次
        // 触发直接 return，之后再也没有第二次——真实会话里译文永远不回填。这里完全不碰
        // DrainDebounce，只等定时器自己响，正是上面那种写法抓不到的路径。
        var svc = new SpeechTranscriptService((_, _) => Task.FromResult("你好，世界。"), Options(debounceMs: 30));
        svc.SubmitSentence(1, "你好，世界", final: true);

        await WaitUntilAsync(() => svc.Snapshot()[0].Target.Length > 0);
        Assert.Equal("你好，世界。", svc.Snapshot()[0].Target);
        svc.Dispose();
    }

    [Fact]
    public async Task A_later_batch_still_gets_translated_after_the_first_one()
    {
        // 一次性闹钟的第二种死法：第一批侥幸赶上了，第二批照样堆在队列里没人管。
        int calls = 0;
        var svc = new SpeechTranscriptService(
            (text, _) =>
            {
                calls++;
                return Task.FromResult(text + "→");
            },
            Options(debounceMs: 30));

        svc.SubmitSentence(1, "第一句", final: true);
        await WaitUntilAsync(() => calls >= 1);
        Assert.Equal("第一句→", svc.Snapshot()[0].Target);

        svc.SubmitSentence(2, "第二句", final: true);
        await WaitUntilAsync(() => calls >= 2);
        Assert.Equal("第二句→", svc.Snapshot()[1].Target);
        svc.Dispose();
    }

    /// <summary>v127: 会话结束后排队/在途的字幕翻译必须取消，不能再按用户额度发请求。</summary>
    [Fact]
    public async Task Dispose_cancels_a_translation_that_is_already_in_flight()
    {
        var started = new TaskCompletionSource();
        var canceled = new TaskCompletionSource();
        var svc = new SpeechTranscriptService(async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
            return "";
        }, Options());

        svc.SubmitSentence(1, "第一句", final: true);
        svc.DrainDebounce();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        svc.Dispose();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>v106: 终稿无译文要标「未翻译」；草稿不能标，否则译文回来前每句都闪一次。</summary>
    [Theory]
    [InlineData(false, "", false)]
    [InlineData(false, "译文", false)]
    [InlineData(true, "", true)]
    [InlineData(true, "   ", true)]
    [InlineData(true, "译文", false)]
    public void Untranslated_is_only_final_lines_without_a_target(bool final, string target, bool expected)
    {
        var line = new SpeechTranscriptLine(1, "source", target, final, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, line.IsUntranslated);
    }

    /// <summary>RunBatchAsync 是 fire-and-forget,断言前轮询到它跑完,不赌调度时机。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
