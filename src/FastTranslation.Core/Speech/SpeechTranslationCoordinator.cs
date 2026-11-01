using System.Net.WebSockets;

namespace FastTranslation.Core.Speech;

/// <summary>语音会话状态机:Idle → Connecting → Streaming → Finishing → Idle,任何致命错误进 Failed。</summary>
public enum SpeechCoordinatorState { Idle, Connecting, Streaming, Finishing, Failed }

/// <summary>
/// 语音翻译会话编排:音频进环形缓冲、ready 后按 100ms 节奏送往传输层、句子转交字幕服务、
/// 断线自动重连一次并按序重放未确认音频。只依赖 Core 抽象,不碰 WPF——界面通过事件订阅。
/// </summary>
public sealed class SpeechTranslationCoordinator : IAsyncDisposable
{
    private const int ReadyTimeoutMs = 10_000;
    private const int PumpPeriodMs = 100;
    private const int ReplaySeconds = 2;
    private const int StopTimeoutMs = 2_000;

    private readonly ISpeechTransport _transport;
    private readonly SpeechTranscriptService _subtitles;
    private readonly TimeProvider _time;
    private readonly Queue<byte[]> _audio = new();
    private readonly object _gate = new();

    private SpeechCoordinatorState _state = SpeechCoordinatorState.Idle;
    private SpeechSessionOptions _options = new(16_000, "en");
    private ITimer? _pump;
    private TaskCompletionSource<bool> _ready;
    private int _bufferBytes;
    private int _bufferCapacityBytes;
    private byte[]? _inflight;
    private int _maxSentenceId;
    private int _sentenceIdOffset;
    private bool _reconnected;

    public SpeechTranslationCoordinator(ISpeechTransport transport, SpeechTranscriptService subtitles, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(subtitles);
        _transport = transport;
        _subtitles = subtitles;
        _time = time ?? TimeProvider.System;
        _ready = CreateReadySource();
        _transport.Received += OnTransportEvent;
        _subtitles.LineUpdated += line => TranscriptUpdated?.Invoke(line);
    }

    public event Action<SpeechTranscriptLine>? TranscriptUpdated;
    public event Action<string>? Failed;
    public event Action<double>? UsageObserved;

    public SpeechCoordinatorState State => _state;
    public double SessionSeconds { get; private set; }

    /// <summary>采集线程的音频入口;缓冲上限按「最近 ReplaySeconds 秒」计,超出丢最旧。</summary>
    public void PushAudio(byte[] block)
    {
        ArgumentNullException.ThrowIfNull(block);
        lock (_gate)
        {
            _audio.Enqueue(block);
            _bufferBytes += block.Length;
            while (_bufferBytes > _bufferCapacityBytes && _audio.Count > 1)
                _bufferBytes -= _audio.Dequeue().Length;
        }
    }

    public async Task StartAsync(SpeechSessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // PCM16 单声道:每秒字节数 = 采样率×2;重放窗口按这个换算。
        _bufferCapacityBytes = options.SourceRate * 2 * ReplaySeconds;
        _state = SpeechCoordinatorState.Connecting;
        _ready = CreateReadySource();
        try
        {
            await _transport.ConnectAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or OperationCanceledException)
        {
            OnFatal("无法连接语音服务,请稍后再试。");
            return;
        }

        if (!await WaitReadyAsync(ct).ConfigureAwait(false))
            return;
        EnterStreaming();
    }

    public async Task StopAsync()
    {
        if (_state is not (SpeechCoordinatorState.Streaming or SpeechCoordinatorState.Connecting))
            return;
        _state = SpeechCoordinatorState.Finishing;
        _pump?.Dispose();
        _pump = null;
        try
        {
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // 服务端可能已先关连接;stop 发不出去不改变「会话已结束」的事实。
        }
        // 立即冲一次防抖:最后一句终稿不必等 500ms。
        _subtitles.DrainDebounce();
        _state = SpeechCoordinatorState.Idle;
    }

    public async ValueTask DisposeAsync()
    {
        _transport.Received -= OnTransportEvent;
        _pump?.Dispose();
        _pump = null;
        try
        {
            if (_state == SpeechCoordinatorState.Streaming)
                await _transport.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromMilliseconds(StopTimeoutMs)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException)
        {
            // 释放路径:停不下来就放着,连接由传输层自己的超时回收。
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnTransportEvent(SpeechTransportEvent e)
    {
        switch (e.Kind)
        {
            case SpeechTransportKind.Ready:
                _ready.TrySetResult(true);
                break;
            case SpeechTransportKind.Sentence:
                if (e.IsFinal && e.SentenceId > _maxSentenceId)
                    _maxSentenceId = e.SentenceId;
                _subtitles.SubmitSentence(e.SentenceId + _sentenceIdOffset, e.Text, e.IsFinal);
                break;
            case SpeechTransportKind.Usage:
                // 服务端 usage 是会话累计值;取 max 防乱序回退。
                if (e.Seconds > SessionSeconds)
                {
                    SessionSeconds = e.Seconds;
                    UsageObserved?.Invoke(SessionSeconds);
                }
                break;
            case SpeechTransportKind.Error:
                OnFatal(string.IsNullOrEmpty(e.Message) ? e.Code : e.Message);
                break;
        }
    }

    private async Task<bool> WaitReadyAsync(CancellationToken ct)
    {
        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromMilliseconds(ReadyTimeoutMs), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            OnFatal("语音服务未就绪,请稍后再试。");
            return false;
        }
        catch (OperationCanceledException)
        {
            // 外部取消:调用方决定后续;这里把状态留在 Connecting,不再启动泵。
            return false;
        }
    }

    private void EnterStreaming()
    {
        _state = SpeechCoordinatorState.Streaming;
        _pump = _time.CreateTimer(_ => _ = DrainAudioCycle(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(PumpPeriodMs));
    }

    private async Task DrainAudioCycle()
    {
        if (_state != SpeechCoordinatorState.Streaming)
            return;
        while (_state == SpeechCoordinatorState.Streaming)
        {
            var block = _inflight;
            if (block is null)
            {
                lock (_gate)
                {
                    if (!_audio.TryDequeue(out block))
                        return;
                    _bufferBytes -= block.Length;
                }
            }
            _inflight = null;
            try
            {
                await _transport.SendAudioAsync(block, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                // 发送失败即断线;块留在 _inflight,重连成功后第一拍先送它,顺序不乱。
                _inflight = block;
                await HandleConnectionLostAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task HandleConnectionLostAsync()
    {
        if (_state != SpeechCoordinatorState.Streaming)
            return;
        _pump?.Dispose();
        _pump = null;
        // 只自动重连一次:第二次断说明网络真的坏了,让用户自己重开会话。
        if (_reconnected)
        {
            OnFatal("语音连接中断。");
            return;
        }
        _reconnected = true;
        // 服务端重连后句号从头计数;偏移量保证字幕行主键单调,不与旧会话撞行。
        _sentenceIdOffset += _maxSentenceId;
        _maxSentenceId = 0;
        _state = SpeechCoordinatorState.Connecting;
        _ready = CreateReadySource();
        try
        {
            await _transport.ConnectAsync(_options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            OnFatal("语音重连失败,请检查网络后重试。");
            return;
        }
        if (!await WaitReadyAsync(CancellationToken.None).ConfigureAwait(false))
            return;
        EnterStreaming();
        // 泵重启后第一拍会把 _inflight 与缓冲里最近 2 秒按原序重放。
    }

    private void OnFatal(string message)
    {
        _pump?.Dispose();
        _pump = null;
        _state = SpeechCoordinatorState.Failed;
        Failed?.Invoke(message);
    }

    private static TaskCompletionSource<bool> CreateReadySource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>测试钩子:手动跑一拍泵,不等真定时器。</summary>
    internal Task DrainAudioForTestAsync() => DrainAudioCycle();
}
