using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastTranslation.Platform.Windows;

/// <summary>Physical-pixel capture result.</summary>
public sealed record CapturedImage(byte[] PngBytes, int Left, int Top, int Width, int Height);

/// <summary>
/// Screen-region capture: a full virtual-screen dim overlay lets the user drag a rectangle, and the
/// pixels under it are blitted with GDI and returned as PNG bytes. This project has no
/// System.Drawing reference, so the capture path is GetDC(NULL) + BitBlt into a compatible bitmap
/// and Imaging.CreateBitmapSourceFromHBitmap, never Graphics.CopyFromScreen.
/// </summary>
public static class ScreenRegionPicker
{
    /// <summary>How long the hidden overlay gets to leave the screen before we blit it.</summary>
    private const int OverlayDismissDelayMs = 60;

    /// <summary>Shows the dimmed overlay; returns null when the user cancels (Esc / right-click / empty rect).</summary>
    public static async Task<CapturedImage?> PickAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        if (dispatcher.CheckAccess()) return await PickCoreAsync(cancellationToken);

        // Off the UI thread: build and pump the window on the dispatcher that owns it.
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

            // Mixed-DPI desktops are approximated with the overlay's own DPI: the overlay is placed
            // at the virtual-screen origin, so every offset inside it scales by one factor.
            var dpi = VisualTreeHelper.GetDpi(overlay);
            var physical = RegionGeometry.ToPhysicalPixels(
                dipSelection, dpi.DpiScaleX, dpi.DpiScaleY, RegionGeometry.VirtualScreenPixels());
            if (physical.Width < RegionGeometry.MinimumCaptureSizePixels
                || physical.Height < RegionGeometry.MinimumCaptureSizePixels) return null;

            // Capture only once the scrim is really gone, otherwise our own tint lands in the shot.
            overlay.Hide();
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(OverlayDismissDelayMs).ConfigureAwait(true);

            var png = CapturePng(physical);
            return png is null ? null : new CapturedImage(png, physical.Left, physical.Top, physical.Width, physical.Height);
        }
        finally
        {
            // Closing a window that the overlay itself already closed is a no-op.
            overlay.Close();
        }
    }

    /// <summary>Blits <paramref name="region"/> off the screen and encodes it as PNG.</summary>
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

    /// <summary>Every GDI handle created here is released on the success path and on every failure path.</summary>
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
            // Select the old object back first: DeleteObject fails on a bitmap still bound to a DC.
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}

/// <summary>A rectangle in physical (device) pixels.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    internal int Right => Left + Width;
    internal int Bottom => Top + Height;
}

/// <summary>
/// Pure drag → capture geometry, kept out of the window so it can be unit-tested without a UI.
/// </summary>
internal static class RegionGeometry
{
    /// <summary>A rectangle thinner than this on either axis is a click, not a capture.</summary>
    internal const int MinimumCaptureSizePixels = 8;

    /// <summary>Corner-agnostic selection rect: <paramref name="anchor"/> may be any corner of the drag.</summary>
    internal static Rect FromDrag(Point anchor, Point current) => new(
        Math.Min(anchor.X, current.X),
        Math.Min(anchor.Y, current.Y),
        Math.Abs(current.X - anchor.X),
        Math.Abs(current.Y - anchor.Y));

    /// <summary>
    /// Maps a selection in overlay DIP coordinates to the physical-pixel rect BitBlt needs, clamped
    /// to the virtual screen. Not pure only in that mixed-DPI desktops collapse onto one scale
    /// (see <see cref="ScreenRegionPicker"/>); the arithmetic itself has no ambient state.
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

    /// <summary>Whole virtual desktop in physical pixels — the coordinate space of the screen DC.</summary>
    internal static PixelRect VirtualScreenPixels() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
}

/// <summary>
/// Full virtual-screen capture overlay. The user drags a rectangle with the left button; Esc, a
/// right click, or a sub-threshold drag cancels. It deliberately touches nothing else: no clipboard,
/// no foreground-window change, no hook state.
/// </summary>
internal sealed class RegionCaptureOverlay : Window
{
    // 0x59 = 89/255 ≈ 0.35 dim. The transparency lives in the window brush instead of
    // Window.Opacity so the selection chrome and the size label stay fully opaque on top of it.
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

        // The overlay must span every monitor, including negative-coordinate ones.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = _layer;
    }

    /// <summary>Completes with the selection in overlay DIP coordinates, or null when cancelled.</summary>
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

    /// <summary>Draws the brand-blue selection, its four corner handles, and the DIP size label.</summary>
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
