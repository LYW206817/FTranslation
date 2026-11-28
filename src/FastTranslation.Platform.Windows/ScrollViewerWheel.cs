using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 让页面级 <see cref="ScrollViewer"/> 在嵌套控件（<see cref="PasswordBox"/>、
/// <see cref="TextBox"/>、<see cref="ListBox"/>、内层 <see cref="ScrollViewer"/>）
/// 无法继续滚动时接管滚轮事件，并对鼠标逐格滚轮做轻量阻尼平滑滚动（v28/v188/v212）。
/// </summary>
public static class ScrollViewerWheel
{
    private const double SmoothDecayRate = 18.0;
    private const double SmoothSnapThreshold = 0.5;
    private const double ExternalScrollTolerance = 1.5;

    /// <summary>是否在嵌套控件无法于该方向滚动时，将滚轮转发给本 <see cref="ScrollViewer"/>。</summary>
    public static readonly DependencyProperty ForwardProperty = DependencyProperty.RegisterAttached(
        "Forward",
        typeof(bool),
        typeof(ScrollViewerWheel),
        new PropertyMetadata(false, OnForwardChanged));

    private static readonly DependencyProperty ScrollerProperty = DependencyProperty.RegisterAttached(
        "Scroller",
        typeof(SmoothScroller),
        typeof(ScrollViewerWheel),
        new PropertyMetadata(null));

    /// <summary>获取是否启用滚轮转发。</summary>
    public static bool GetForward(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ForwardProperty);
    }

    /// <summary>设置是否启用滚轮转发。</summary>
    public static void SetForward(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ForwardProperty, value);
    }

    /// <summary>判断滚动器在给定 <paramref name="delta"/> 方向上是否还有剩余滚动空间。</summary>
    public static bool CanScroll(double verticalOffset, double scrollableHeight, int delta)
    {
        if (delta == 0 || scrollableHeight <= 0.5) return false;
        return delta > 0 ? verticalOffset > 0.5 : verticalOffset < scrollableHeight - 0.5;
    }

    /// <summary>按系统滚轮行数与视口高度计算标准一格（120 delta）的垂直步进 DIP。</summary>
    public static double ComputeWheelStep(int wheelScrollLines, double viewportHeight) =>
        wheelScrollLines > 0
            ? wheelScrollLines * 16.0
            : wheelScrollLines == -1
                ? Math.Max(0.0, viewportHeight * 0.9)
                : 0.0;

    /// <summary>按 <paramref name="delta"/> 与步长计算夹紧后的目标垂直偏移。</summary>
    public static double ComputeTargetOffset(double currentOffset, double scrollableHeight, int delta, double step)
    {
        if (scrollableHeight <= 0 || step <= 0 || delta == 0)
        {
            return Math.Clamp(currentOffset, 0.0, Math.Max(0.0, scrollableHeight));
        }

        var notches = (double)delta / Mouse.MouseWheelDeltaForOneLine;
        return Math.Clamp(currentOffset - notches * step, 0.0, scrollableHeight);
    }

    /// <summary>帧率无关的指数阻尼插值；距离目标不足 0.5 DIP 时直接吸附，避免空闲帧空转。</summary>
    internal static double AdvanceSmoothOffset(double current, double target, double elapsedSeconds)
    {
        var remaining = target - current;
        if (Math.Abs(remaining) <= SmoothSnapThreshold) return target;
        if (elapsedSeconds <= 0) return current;

        var dt = Math.Min(elapsedSeconds, 0.05);
        var factor = 1.0 - Math.Exp(-SmoothDecayRate * dt);
        var next = current + remaining * factor;
        return Math.Abs(target - next) <= SmoothSnapThreshold ? target : next;
    }

    private static void OnForwardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            StopScroller(viewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        // Shift+滚轮留给水平滚动手势，不截成上下翻页。
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (sender is not ScrollViewer outer) return;

        var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject, outer);
        if (inner is not null && CanScroll(inner.VerticalOffset, inner.ScrollableHeight, e.Delta)) return;

        if (outer.ScrollableHeight <= 0) return;

        var step = ComputeWheelStep(SystemParameters.WheelScrollLines, outer.ViewportHeight);
        if (step <= 0) return;

        e.Handled = true;

        // 精密触控板的小幅高频增量（<120）或无窗口测试直接跟随，不做额外阻尼。
        if (Math.Abs(e.Delta) < Mouse.MouseWheelDeltaForOneLine || PresentationSource.FromVisual(outer) is null)
        {
            StopScroller(outer);
            outer.ScrollToVerticalOffset(ComputeTargetOffset(outer.VerticalOffset, outer.ScrollableHeight, e.Delta, step));
            return;
        }

        var scroller = GetOrCreateScroller(outer);
        scroller.AddWheelDelta(e.Delta, step);
    }

    private static SmoothScroller GetOrCreateScroller(ScrollViewer viewer)
    {
        if (viewer.GetValue(ScrollerProperty) is SmoothScroller existing) return existing;
        var created = new SmoothScroller(viewer);
        viewer.SetValue(ScrollerProperty, created);
        return created;
    }

    private static void StopScroller(ScrollViewer viewer)
    {
        if (viewer.GetValue(ScrollerProperty) is SmoothScroller existing)
        {
            existing.Stop();
        }
    }

    private static ScrollViewer? FindInnerScrollViewer(DependencyObject? node, ScrollViewer outer)
    {
        for (var current = node; current is not null && !ReferenceEquals(current, outer); current = GetParent(current))
        {
            if (current is ScrollViewer viewer) return viewer;
        }
        return null;
    }

    // 文字里的 Run / Hyperlink 是 ContentElement 而非 Visual；直接喂给 VisualTreeHelper 会抛
    // InvalidOperationException，先经内容树回到宿主 Visual 再继续上溯。
    private static DependencyObject? GetParent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        ContentElement ce => ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(node),
        _ => LogicalTreeHelper.GetParent(node),
    };

    private sealed class SmoothScroller
    {
        private readonly ScrollViewer _viewer;
        private readonly Stopwatch _clock = new();
        private bool _active;
        private double _targetOffset;
        private double _lastAppliedOffset;
        private double _lastSeconds;

        public SmoothScroller(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.IsVisibleChanged += (_, e) => { if (e.NewValue is false) Stop(); };
            viewer.Unloaded += (_, _) => Stop();
        }

        public void AddWheelDelta(int delta, double step)
        {
            var current = _viewer.VerticalOffset;
            var baseOffset = _active && Math.Abs(current - _lastAppliedOffset) <= ExternalScrollTolerance
                ? _targetOffset
                : current;

            _targetOffset = ComputeTargetOffset(baseOffset, _viewer.ScrollableHeight, delta, step);
            if (Math.Abs(_targetOffset - current) <= SmoothSnapThreshold)
            {
                _viewer.ScrollToVerticalOffset(_targetOffset);
                Stop();
                return;
            }

            _lastAppliedOffset = current;
            if (!_active)
            {
                _active = true;
                _clock.Restart();
                _lastSeconds = 0;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        public void Stop()
        {
            if (!_active) return;
            _active = false;
            _clock.Stop();
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_active || !_viewer.IsVisible)
            {
                Stop();
                return;
            }

            var current = _viewer.VerticalOffset;
            // 用户拖滚动条或外部代码跳位时立刻让出控制权，不把滚动条拉回去。
            if (Math.Abs(current - _lastAppliedOffset) > ExternalScrollTolerance)
            {
                Stop();
                return;
            }

            var now = _clock.Elapsed.TotalSeconds;
            var dt = now - _lastSeconds;
            _lastSeconds = now;

            var target = Math.Clamp(_targetOffset, 0.0, Math.Max(0.0, _viewer.ScrollableHeight));
            var next = AdvanceSmoothOffset(current, target, dt);
            _lastAppliedOffset = next;
            _viewer.ScrollToVerticalOffset(next);

            if (Math.Abs(target - next) <= SmoothSnapThreshold)
            {
                Stop();
            }
        }
    }
}
