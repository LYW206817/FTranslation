using System.Net.WebSockets;
using FastTranslation.Core.Speech;
using Xunit;

namespace FastTranslation.Core.Tests.Speech;

public sealed class SpeechCoordinatorTests
{
    private static readonly SilentTimeProvider SilentTime = new();

    /// <summary>CreateTimer 永不触发:后台泵不存在,音频只由 DrainAudioForTestAsync 手动驱动,测试无竞态。</summary>
    private sealed class SilentTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new SilentTimer();
    }

    private sealed class SilentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SpeechSessionOptions Options() => new(16_000, "en", DebounceMs: 0);

    [Fact]
    public async Task Buffers_audio_until_ready_then_pumps_it()
    {
        var transport = new FakeTransport();
        var calls = new List<string>();
        using var subtitles = MakeSubtitles(calls);
        await using var coordinator = new SpeechTranslationCoordinator(transport, subtitles, SilentTime);

        // ready 之前推的音频只进缓冲,StartAsync 返回时泵启动后第一拍才送出。
        coordinator.PushAudio(new byte[] { 1 });
        await coordinator.StartAsync(Options());
        await coordinator.DrainAudioForTestAsync();
        Assert.Single(transport.SentAudio);

        coordinator.PushAudio(new byte[] { 2 });
        await coordinator.DrainAudioForTestAsync();
        Assert.Equal(2, transport.SentAudio.Count);
    }

    [Fact]
    public async Task Final_sentences_reach_the_transcript_service()
    {
        var transport = new FakeTransport();
        var calls = new List<string>();
        using var subtitles = MakeSubtitles(calls);
        await using var coordinator = new SpeechTranslationCoordinator(transport, subtitles, SilentTime);
        await coordinator.StartAsync(Options());

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Sentence, 1, "你好", IsFinal: true));
        subtitles.DrainDebounce();
        Assert.Single(calls);
        var line = Assert.Single(subtitles.Snapshot());
        Assert.Equal("T:你好", line.Target);

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Sentence, 2, "部分", IsFinal: false));
        Assert.Single(calls);
        Assert.Equal("", subtitles.Snapshot().Single(l => l.SentenceId == 2).Target);
    }

    [Fact]
    public async Task Error_event_fails_the_session_and_stops_the_pump()
    {
        var transport = new FakeTransport();
        var calls = new List<string>();
        using var subtitles = MakeSubtitles(calls);
        await using var coordinator = new SpeechTranslationCoordinator(transport, subtitles, SilentTime);
        await coordinator.StartAsync(Options());
        var failures = new List<string>();
        coordinator.Failed += failures.Add;

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Error, Code: "task_failed", Message: "boom"));
        await WaitUntilAsync(() => coordinator.State == SpeechCoordinatorState.Failed);
        Assert.Equal(["boom"], failures);

        coordinator.PushAudio(new byte[] { 9 });
        await coordinator.DrainAudioForTestAsync();
        Assert.Empty(transport.SentAudio);
    }

    [Fact]
    public async Task Reconnects_once_and_replays_pending_audio_in_order()
    {
        var transport = new FakeTransport();
        var calls = new List<string>();
        using var subtitles = MakeSubtitles(calls);
        await using var coordinator = new SpeechTranslationCoordinator(transport, subtitles, SilentTime);
        await coordinator.StartAsync(Options());

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Sentence, 1, "第一句", IsFinal: true));
        coordinator.PushAudio(new byte[] { 1 });
        await coordinator.DrainAudioForTestAsync();
        Assert.Single(transport.SentAudio);
        subtitles.DrainDebounce();
        Assert.Equal("T:第一句", subtitles.Snapshot().Single().Target);

        transport.FailNextSend = true;
        coordinator.PushAudio(new byte[] { 2 });
        await coordinator.DrainAudioForTestAsync();
        await WaitUntilAsync(() => transport.ConnectCount == 2);

        await WaitUntilAsync(() => coordinator.State == SpeechCoordinatorState.Streaming);
        await coordinator.DrainAudioForTestAsync();
        // 未确认的第 2 块被原样重放,而已确认的第 1 块不重发。
        Assert.Equal([new byte[] { 1 }, new byte[] { 2 }], transport.SentAudio);

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Sentence, 1, "重连后第一句", IsFinal: true));
        subtitles.DrainDebounce();
        Assert.Equal(2, subtitles.Snapshot().Count);
        Assert.Equal(2, subtitles.Snapshot()[1].SentenceId);
    }

    [Fact]
    public async Task Stop_sends_stop_frame_and_flushes_pending_finals()
    {
        var transport = new FakeTransport();
        var calls = new List<string>();
        using var subtitles = MakeSubtitles(calls);
        await using var coordinator = new SpeechTranslationCoordinator(transport, subtitles, SilentTime);
        await coordinator.StartAsync(Options());

        transport.Emit(new SpeechTransportEvent(SpeechTransportKind.Sentence, 1, "最后一句", IsFinal: true));
        await coordinator.StopAsync();

        Assert.True(transport.StopCalled);
        Assert.Single(calls);
        Assert.Equal(SpeechCoordinatorState.Idle, coordinator.State);
    }

    private static SpeechTranscriptService MakeSubtitles(List<string> calls)
        => new((text, _) =>
        {
            calls.Add(text);
            return Task.FromResult("T:" + text);
        }, Options());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeTransport : ISpeechTransport
    {
        public bool FailNextSend;
        public int ConnectCount;
        public bool StopCalled;
        public readonly List<byte[]> SentAudio = new();
        private readonly List<TaskCompletionSource> _connected = new();

        public event Action<SpeechTransportEvent>? Received;

        public void Emit(SpeechTransportEvent e) => Received?.Invoke(e);

        public Task ConnectAsync(SpeechSessionOptions options, CancellationToken ct = default)
        {
            ConnectCount++;
            Emit(new SpeechTransportEvent(SpeechTransportKind.Ready));
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new WebSocketException();
            }
            SentAudio.Add(pcm.ToArray());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalled = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
