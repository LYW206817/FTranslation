using System.Collections.Concurrent;

namespace FastTranslation.Core.Speech;

/// <summary>字幕面板上的一行；IsFinal=false 时 Target 恒为空串（部分结果绝不送翻译）。</summary>
public sealed record SpeechTranscriptLine(int SentenceId, string Source, string Target, bool IsFinal, DateTimeOffset StartedAt)
{
    /// <summary>
    /// 终稿但没有译文＝翻译失败或队列丢弃。界面必须显式标「未翻译」：
    /// 留一行空白会让用户以为字幕丢了，而不是没译成。
    /// </summary>
    public bool IsUntranslated => IsFinal && string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// 语音会话参数；数值均有产品含义，见 docs/superpowers/specs 的语音方案。
/// 这里刻意不带「目标语言」——识别语种必须由 ASR 自己判断，把翻译目标当语种提示
/// 会让英文音频被按中文识别（v81 的 bug 就是这么来的）；译成哪种语言由翻译回调自己决定。
/// </summary>
public sealed record SpeechSessionOptions(
    int SourceRate,
    int MaxSentences = 200,
    int DebounceMs = 500,
    int MaxConcurrency = 2,
    int QueueCapacity = 8);

/// <summary>
/// 语音字幕会话：终稿防抖合并送 MT、并发上限、有界队列丢最旧。
/// 故意不复用 TranslationService——它「新请求取消上一在途」的语义会把连续语音的
/// 翻译互相打断，字幕恰恰要上一句翻完、这一句继续翻。
/// </summary>
public sealed class SpeechTranscriptService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, SpeechTranscriptLine> _lines = new();
    private readonly List<int> _pending = new();
    private readonly ConcurrentQueue<List<int>> _queue = new();
    private readonly Func<string, CancellationToken, Task<string>> _translate;
    private readonly SpeechSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private int _inFlight;
    private int _dropped;

    /// <summary>任一行更新（部分结果或译文回填）都会触发；快照在锁外投递，UI 拿到的行已定稿。</summary>
    public event Action<SpeechTranscriptLine>? LineUpdated;

    public SpeechTranscriptService(
        Func<string, CancellationToken, Task<string>> translate,
        SpeechSessionOptions options,
        TimeProvider? time = null)
    {
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _options = options;
        _time = time ?? TimeProvider.System;
        if (_options.DebounceMs > 0)
        {
            // v94: 定时器挂成无限期不启动，改由「终稿入队」来调度（见 SubmitSentence）。
            // 原来这里在构造时就起一个 500ms 的一次性倒计时（period=Infinite），而服务是在会话
            // 开始那一刻构造的——500ms 后唯一那次触发时 _pending 还是空的，直接 return，
            // 之后再也没有第二次：整个会话的终稿全堆在 _pending 里等一次不会来的闹钟，
            // 字幕只有原文、译文永远不回填（实测 final 帧 3 次、翻译回调调用 0 次）。
            _timer = _time.CreateTimer(DrainDebounce, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public int DroppedCount => _dropped;

    /// <summary>按 SentenceId 递增返回当前字幕行（超过 MaxSentences 淘汰最旧）。</summary>
    public IReadOnlyList<SpeechTranscriptLine> Snapshot()
    {
        lock (_gate)
        {
            return _lines.Values.OrderBy(l => l.SentenceId).ToArray();
        }
    }

    /// <summary>ASR 每帧结果都进这里；非终稿只刷新行文本，绝不进翻译队列。</summary>
    public void SubmitSentence(int sentenceId, string text, bool final)
    {
        SpeechTranscriptLine line;
        bool schedule = false;
        lock (_gate)
        {
            if (_lines.TryGetValue(sentenceId, out var existing))
            {
                line = final ? existing with { Source = text, IsFinal = true } : existing with { Source = text };
            }
            else
            {
                line = new SpeechTranscriptLine(sentenceId, text, "", final, _time.GetUtcNow());
            }

            _lines[sentenceId] = line;
            EvictOverflow();
            if (final && !_pending.Contains(sentenceId))
            {
                // 只有本批的第一句才设闹钟：同一防抖窗口里后续的终稿直接搭车，到点合成一批送 MT。
                // 批取走后 _pending 清空，下一句终稿重新计时的就是这里。
                schedule = _pending.Count == 0;
                _pending.Add(sentenceId);
            }
        }

        // Change 放在锁外：定时器回调会进 DrainDebounce 抢同一把锁，持锁调度可能自锁。
        if (schedule)
        {
            _timer?.Change(TimeSpan.FromMilliseconds(_options.DebounceMs), Timeout.InfiniteTimeSpan);
        }

        LineUpdated?.Invoke(line);
    }

    /// <summary>防抖窗口到期（或测试手动驱动）：把攒下的终稿合成一批送 MT。</summary>
    public void DrainDebounce(object? state = null)
    {
        List<int> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
        }

        if (batch.Count == 0)
        {
            return;
        }

        // 有界队列：满时丢最旧的一批（其行保持未翻译，由 UI 呈现），新批次优先
        while (_queue.Count >= _options.QueueCapacity && _queue.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
        }

        _queue.Enqueue(batch);
        TryStartNext();
    }

    public void Dispose() => _timer?.Dispose();

    private void TryStartNext()
    {
        lock (_gate)
        {
            if (_inFlight >= _options.MaxConcurrency || !_queue.TryDequeue(out var dequeued))
            {
                return;
            }

            _inFlight++;
            _ = RunBatchAsync(dequeued);
        }
    }

    private async Task RunBatchAsync(List<int> batch)
    {
        try
        {
            string merged;
            lock (_gate)
            {
                merged = string.Join("\n", batch.Select(id => _lines[id].Source));
            }

            string translated = await _translate(merged, CancellationToken.None).ConfigureAwait(false);
            Backfill(batch, translated);
        }
        catch (Exception)
        {
            // 翻译失败：终稿行保持 Target 为空，UI 呈现「未翻译」，不打断会话
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }

            TryStartNext();
        }
    }

    /// <summary>§5.2 回填规则：N 句按换行切；句数不符则整段回填第一句（不赌对齐），其余保持空。</summary>
    private void Backfill(List<int> batch, string translated)
    {
        string[] parts = translated.Replace("\r\n", "\n").Split('\n');
        bool aligned = parts.Length == batch.Count;
        List<SpeechTranscriptLine> updated = [];
        lock (_gate)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                if (!_lines.TryGetValue(batch[i], out var line))
                {
                    continue;
                }

                string target = aligned ? parts[i].Trim() : (i == 0 ? translated.Trim() : "");
                line = line with { Target = target };
                _lines[batch[i]] = line;
                updated.Add(line);
            }
        }

        foreach (var line in updated)
        {
            LineUpdated?.Invoke(line);
        }
    }

    private void EvictOverflow()
    {
        while (_lines.Count > _options.MaxSentences)
        {
            int oldest = _lines.Keys.Min();
            _lines.Remove(oldest);
        }
    }
}
