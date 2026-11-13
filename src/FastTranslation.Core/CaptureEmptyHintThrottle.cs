namespace FastTranslation.Core;

/// <summary>
/// 「没读到选中文字」提示的节流：同类提示 24 小时内最多一次（规格 §L4）。
/// 时钟可注入，测试里不用真的等一天。
/// </summary>
public sealed class CaptureEmptyHintThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateTimeOffset _lastShown = DateTimeOffset.MinValue;

    public CaptureEmptyHintThrottle(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>允许提示时返回 true 并记下本次时间，否则返回 false。</summary>
    public bool ShouldShow()
    {
        // 工具栏点击和热键来自两条 UI 路径，锁住时间戳避免同一瞬间两条都放行。
        lock (_gate)
        {
            var now = _now();
            if (now - _lastShown < Window) return false;
            _lastShown = now;
            return true;
        }
    }
}