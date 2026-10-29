using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastTranslation.Platform.Windows;

/// <summary>物理像素坐标下的截图结果。</summary>
public sealed record CapturedImage(byte[] PngBytes, int Left, int Top, int Width, int Height);

/// <summary>
/// 屏幕区域截图：一个铺满整个虚拟屏幕的变暗遮罩让用户拖出一个矩形，
/// 再用 GDI 把矩形下方的像素拷出来，以 PNG 字节返回。本项目不引用
/// System.Drawing，所以截图路径是 GetDC(NULL) + BitBlt 到兼容位图，
/// 再用 Imaging.CreateBitmapSourceFromHBitmap，绝不用 Graphics.CopyFromScreen。
/// </summary>
public static class ScreenRegionPicker
{
    /// <summary>隐藏遮罩后要等多久让它真正离开屏幕，再执行 blit。</summary>
    private const int OverlayDismissDelayMs = 60;

    /// <summary>显示变暗遮罩；用户取消时返回 null(Esc / 右键 / 空矩形)。</summary>
    public static async Task<CapturedImage?> PickAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        if (dispatcher.CheckAccess()) return await PickCoreAsync(cancellationToken);

        // 不在 UI 线程上：在拥有该窗口的 dispatcher 上创建并驱动它。
        var inner = await dispatcher.InvokeAsync(() => PickCoreAsync(cancellationToken));
        return await inner;
    }

    private static async Task<CapturedImage?> PickCoreAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return null;

        var overlay = new RegionCaptureOverlay();
        using var registration = cancellationToken.Register(static state => ((RegionCaptureOverlay)state!).Cancel(), overlay);
        try
        {
            overlay.Show();
            overlay.Activate();

            var selection = await overlay.Selection;
            if (selection is not { } dipSelection) return null;

            // 混合 DPI 桌面按遮罩自身的 DPI 近似处理：遮罩锚定在虚拟屏幕原点，
            // 所以它内部的所有偏移都按同一个系数缩放。
            var dpi = VisualTreeHelper.GetDpi(overlay);
            var physical = RegionGeometry.ToPhysicalPixels(
                dipSelection, dpi.DpiScaleX, dpi.DpiScaleY, RegionGeometry.VirtualScreenPixels());
            if (physical.Width < RegionGeometry.MinimumCaptureSizePixels
                || physical.Height < RegionGeometry.MinimumCaptureSizePixels) return null;

            // 只有等遮罩真的消失后再截图，否则我们自己的着色会进画面。
            overlay.Hide();
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(OverlayDismissDelayMs).ConfigureAwait(true);

            var png = CapturePng(physical);
            return png is null ? null : new CapturedImage(png, physical.Left, physical.Top, physical.Width, physical.Height);
        }
        finally
        {
            // 遮罩自己已经关掉的窗口再关一次是无害的。
            overlay.Close();
        }
    }

    /// <summary>把 <paramref name="region"/> 从屏幕上 blit 下来并编码成 PNG。</summary>
    private static byte[]? CapturePng(PixelRect region)
    {
        var source = CaptureBitmap(region);
        if (source is null) return null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>这里创建的每个 GDI 句柄，无论成功还是失败路径都会释放。</summary>
    private static BitmapSource? CaptureBitmap(PixelRect region)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return null;

            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (bitmap == IntPtr.Zero) return null;

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.BitBlt(
                    memoryDc, 0, 0, region.Width, region.Height,
                    screenDc, region.Left, region.Top, NativeMethods.SRCCOPY)) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            // 先把旧对象选回去：位图还绑在 DC 上时 DeleteObject 会失败。
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}

/// <summary>物理(设备)像素坐标下的矩形。</summary>
internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    internal int Right => Left + Width;
    internal int Bottom => Top + Height;
}

/// <summary>
/// 纯拖拽 → 截图的几何计算，独立于窗口，以便脱离 UI 做单测。
/// </summary>
internal static class RegionGeometry
{
    /// <summary>任一轴比这更细的矩形算点击，不算截图。</summary>
    internal const int MinimumCaptureSizePixels = 8;

    /// <summary>与角无关的选区矩形：<paramref name="anchor"/> 可以是拖拽的任意一角。</summary>
    internal static Rect FromDrag(Point anchor, Point current) => new(
        Math.Min(anchor.X, current.X),
        Math.Min(anchor.Y, current.Y),
        Math.Abs(current.X - anchor.X),
        Math.Abs(current.Y - anchor.Y));

    /// <summary>
    /// 把遮罩 DIP 坐标下的选区映射成 BitBlt 需要的物理像素矩形，并钳制
    /// 到虚拟屏幕范围内。唯一不纯的地方是混合 DPI 桌面会塌缩到同一个缩放系数
    /// (见 <see cref="ScreenRegionPicker"/>)；算术本身不依赖环境状态。
    /// </summary>
    internal static PixelRect ToPhysicalPixels(Rect dipSelection, double dpiScaleX, double dpiScaleY, PixelRect virtualScreen)
    {
        var scaleX = dpiScaleX > 0 ? dpiScaleX : 1;
        var scaleY = dpiScaleY > 0 ? dpiScaleY : 1;

        var left = virtualScreen.Left + (int)Math.Round(dipSelection.Left * scaleX, MidpointRounding.AwayFromZero);
        var top = virtualScreen.Top + (int)Math.Round(dipSelection.Top * scaleY, MidpointRounding.AwayFromZero);
        var right = virtualScreen.Left + (int)Math.Round(dipSelection.Right * scaleX, MidpointRounding.AwayFromZero);
        var bottom = virtualScreen.Top + (int)Math.Round(dipSelection.Bottom * scaleY, MidpointRounding.AwayFromZero);

        left = Math.Clamp(left, virtualScreen.Left, virtualScreen.Right);
        right = Math.Clamp(right, virtualScreen.Left, virtualScreen.Right);
        top = Math.Clamp(top, virtualScreen.Top, virtualScreen.Bottom);
        bottom = Math.Clamp(bottom, virtualScreen.Top, virtualScreen.Bottom);

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>整个虚拟桌面(物理像素)——屏幕 DC 的坐标空间。</summary>
    internal static PixelRect VirtualScreenPixels() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
}

/// <summary>
/// 铺满整个虚拟屏幕的截图遮罩。用户按住左键拖出矩形；Esc、
/// 右键点击、或拖动距离过小都会取消。它刻意不碰别的东西：不动剪贴板、
/// 不改前台窗口、不改钩子状态。
/// </summary>
internal sealed class RegionCaptureOverlay : Window
{
    // 0x59 = 89/255 ≈ 0.35 的暗度。透明度放在窗口画刷里而不是
    // Window.Opacity 上，这样选区边框和尺寸标签才能完全实心地盖在上面。
    private static readonly SolidColorBrush Scrim = CreateBrush(Color.FromArgb(89, 0, 0, 0));

    private readonly TaskCompletionSource<Rect?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SelectionLayer _layer = new();
    private Point? _anchor;
    private bool _finished;

    internal RegionCaptureOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Scrim;
        Cursor = Cursors.Cross;
        Focusable = true;

        // 遮罩必须铺满所有显示器，包括坐标为负的那些。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = _layer;
    }

    /// <summary>以遮罩 DIP 坐标下的选区完成；取消时结果为 null。</summary>
    internal Task<Rect?> Selection => _completion.Task;

    internal void Cancel() => Complete(null);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!_finished)
        {
            _anchor = e.GetPosition(this);
            _layer.SetSelection(RegionGeometry.FromDrag(_anchor.Value, _anchor.Value));
            CaptureMouse();
            e.Handled = true;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_finished && _anchor is { } anchor)
        {
            _layer.SetSelection(RegionGeometry.FromDrag(anchor, e.GetPosition(this)));
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_finished && _anchor is { } anchor)
        {
            var selection = RegionGeometry.FromDrag(anchor, e.GetPosition(this));
            ReleaseMouseCapture();
            _anchor = null;
            Complete(selection is { Width: > 0, Height: > 0 } ? selection : null);
            e.Handled = true;
        }

        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Complete(null);
        e.Handled = true;
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Complete(null);
        base.OnClosed(e);
    }

    private void Complete(Rect? selection)
    {
        if (_finished) return;
        _finished = true;
        _anchor = null;
        _completion.TrySetResult(selection);
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>绘制品牌蓝选区、四个角柄，以及以 DIP 计的尺寸标签。</summary>
    private sealed class SelectionLayer : FrameworkElement
    {
        private static readonly SolidColorBrush Brand = CreateBrush(Color.FromRgb(0x25, 0x62, 0xFF));
        private static readonly SolidColorBrush LabelBackground = CreateBrush(Colors.White);
        private static readonly SolidColorBrush LabelForeground = CreateBrush(Color.FromRgb(0x1B, 0x25, 0x36));

        private const double BorderThickness = 1.5;
        private const double HandleSize = 8;
        private const double LabelFontSize = 11;
        private const double LabelCorner = 4;
        private const double LabelPadding = 4;

        private Rect? _selection;

        internal void SetSelection(Rect? selection)
        {
            _selection = selection;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_selection is not { } selection || selection.Width < 1 || selection.Height < 1) return;

            var pen = new Pen(Brand, BorderThickness);
            pen.Freeze();
            drawingContext.DrawRectangle(null, pen, selection);

            const double half = HandleSize / 2;
            foreach (var corner in new[] { selection.TopLeft, selection.TopRight, selection.BottomLeft, selection.BottomRight })
            {
                drawingContext.DrawRectangle(Brand, null, new Rect(corner.X - half, corner.Y - half, HandleSize, HandleSize));
            }

            DrawSizeLabel(drawingContext, selection);
        }

        private void DrawSizeLabel(DrawingContext drawingContext, Rect selection)
        {
            var text = new FormattedText(
                string.Format(CultureInfo.InvariantCulture, "{0} x {1}", Math.Round(selection.Width), Math.Round(selection.Height)),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FtTheme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                LabelFontSize,
                LabelForeground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var width = Math.Ceiling(text.Width) + LabelPadding * 2;
            var height = Math.Ceiling(text.Height) + LabelPadding / 2;
            var x = Math.Clamp(selection.Right - width, 0, Math.Max(0, RenderSize.Width - width));
            var y = selection.Bottom + LabelPadding;
            if (y + height > RenderSize.Height) y = Math.Max(0, selection.Bottom - height - LabelPadding * 2);

            drawingContext.DrawRoundedRectangle(
                LabelBackground, null, new Rect(x, y, width, height), LabelCorner, LabelCorner);
            drawingContext.DrawText(text, new Point(x + LabelPadding, y + LabelPadding / 4));
        }
    }
}
