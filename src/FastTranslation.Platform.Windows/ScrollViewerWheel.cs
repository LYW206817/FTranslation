using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 让页面级 <see cref="ScrollViewer"/> 在嵌套控件（<see cref="PasswordBox"/>、
/// <see cref="TextBox"/>、<see cref="ListBox"/>、内层 <see cref="ScrollViewer"/>）
/// 无法继续滚动时接管滚轮事件（v28/v188）。
/// </summary>
/// <remarks>
/// <para>
/// WPF 的 <see cref="PasswordBox"/>（模板内含 <c>PART_ContentHost</c> <see cref="ScrollViewer"/>）
/// 以及已滚到顶/底的内层滚动器都会把 <see cref="UIElement.MouseWheelEvent"/> 标成已处理，
/// 导致指针停在输入框上时整页滚轮完全卡死。
/// </para>
/// <para>
/// 改在隧道事件 <see cref="UIElement.PreviewMouseWheelEvent"/> 提前分流，并按
/// <c>e.Delta / 120.0</c> 成比例换算步长（保留普通鼠标一格 = 3×16 DIP，同时让精密触控板的
/// 小幅高频滚轮也能平滑推进而非整格跳变）。
/// </para>
/// </remarks>
public static class ScrollViewerWheel
{
    /// <summary>是否在嵌套控件无法于该方向滚动时，将滚轮转发给本 <see cref="ScrollViewer"/>。</summary>
    public static readonly DependencyProperty ForwardProperty = DependencyProperty.RegisterAttached(
        "Forward",
        typeof(bool),
        typeof(ScrollViewerWheel),
        new PropertyMetadata(false, OnForwardChanged));

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

    private static void OnForwardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) viewer.PreviewMouseWheel += OnPreviewMouseWheel;
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
        outer.ScrollToVerticalOffset(ComputeTargetOffset(outer.VerticalOffset, outer.ScrollableHeight, e.Delta, step));
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
}
