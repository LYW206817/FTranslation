using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>Design tokens shared by the overlay surfaces (matches App.xaml palette).</summary>
internal static class FtTheme
{
    // The overlay surfaces are built in code, so they cannot use the XAML brushes in App.xaml.
    // They read the same hex values from ThemePalette instead (one palette for the whole app),
    // and resolve them lazily so a palette picked at startup applies to every surface.
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    internal static SolidColorBrush Brand => Freeze(ThemePalette.Active.Brand);
    internal static SolidColorBrush BrandHover => Freeze(ThemePalette.Active.BrandHover);
    internal static SolidColorBrush BrandPressed => Freeze(ThemePalette.Active.BrandPressed);
    internal static SolidColorBrush BrandFill => Freeze(ThemePalette.Active.BrandFill);
    internal static SolidColorBrush Accent => Freeze(ThemePalette.Active.Accent);
    internal static SolidColorBrush Bg => Freeze(ThemePalette.Active.WindowBg);
    internal static SolidColorBrush Surface => Freeze(ThemePalette.Active.Surface);
    internal static SolidColorBrush Hair => Freeze(ThemePalette.Active.Hair);
    internal static SolidColorBrush HairStrong => Freeze(ThemePalette.Active.HairStrong);
    internal static SolidColorBrush BorderStrong => Freeze(ThemePalette.Active.BorderStrong);
    internal static SolidColorBrush Hover => Freeze(ThemePalette.Active.Hover);
    internal static SolidColorBrush TextPrimary => Freeze(ThemePalette.Active.TextPrimary);
    internal static SolidColorBrush TextSecondary => Freeze(ThemePalette.Active.TextSecondary);
    internal static SolidColorBrush TextMuted => Freeze(ThemePalette.Active.TextMuted);
    internal static SolidColorBrush Danger => Freeze(ThemePalette.Active.Danger);
    internal static SolidColorBrush Notice => Freeze(ThemePalette.Active.Notice);
    internal static readonly FontFamily UiFont = new("Segoe UI, Microsoft YaHei UI");

    private static SolidColorBrush Freeze(string hex)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Cache[hex] = b;
            return b;
        }
    }

    private static ControlTemplate ButtonTemplate(CornerRadius corner)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.CornerRadiusProperty, corner);
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        return new ControlTemplate(typeof(Button)) { VisualTree = bd };
    }

    /// <summary>Quiet pill button: transparent, fills on hover. Used inside the toolbar.</summary>
    internal static Style PillGhost() => TemplateStyle(
        bg: null, fg: TextPrimary, border: null, corner: new CornerRadius(6),
        hoverBg: Hover, hoverBorder: null, pressedBg: BrandFill);

    /// <summary>Standard secondary button (white, hairline-strong border).</summary>
    internal static Style Ghost() => TemplateStyle(
        bg: Surface, fg: TextPrimary, border: BorderStrong, corner: new CornerRadius(6),
        hoverBg: Hover, hoverBorder: Accent, pressedBg: BrandFill);

    /// <summary>Primary brand button.</summary>
    internal static Style Primary() => TemplateStyle(
        bg: Brand, fg: Brushes.White, border: Brand, corner: new CornerRadius(6),
        hoverBg: BrandHover, hoverBorder: null, pressedBg: BrandPressed,
        disabledBg: Freeze("#A9C0F2"), disabledFg: Freeze("#E9EEFB"));

    /// <summary>Circular quiet button (card ✕).</summary>
    internal static Style RoundGhost() => TemplateStyle(
        bg: null, fg: TextSecondary, border: null, corner: new CornerRadius(12),
        hoverBg: Hover, hoverBorder: null, pressedBg: BrandFill,
        hoverFg: TextPrimary);

    private static Style TemplateStyle(
        Brush? bg, Brush fg, Brush? border, CornerRadius corner,
        Brush hoverBg, Brush? hoverBorder, Brush pressedBg,
        Brush? hoverFg = null, Brush? disabledBg = null, Brush? disabledFg = null)
    {
        var s = new Style(typeof(Button));
        s.Setters.Add(new Setter(Button.ForegroundProperty, fg));
        s.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));
        s.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
        s.Setters.Add(new Setter(Button.FocusableProperty, false));
        s.Setters.Add(new Setter(Button.TemplateProperty, ButtonTemplate(corner)));
        // ALWAYS set Background (transparent counts): omitting it lets the Fluent theme's
        // grey Button background bleed through TemplateBinding (v18 "grey boxes" bug)
        s.Setters.Add(new Setter(Button.BackgroundProperty, bg ?? Brushes.Transparent));
        if (border is not null)
        {
            s.Setters.Add(new Setter(Button.BorderBrushProperty, border));
            s.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
        }
        else
        {
            s.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
        }

        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty, hoverBg));
        if (hoverBorder is not null) hover.Setters.Add(new Setter(Button.BorderBrushProperty, hoverBorder));
        if (hoverFg is not null) hover.Setters.Add(new Setter(Button.ForegroundProperty, hoverFg));
        s.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Button.BackgroundProperty, pressedBg));
        s.Triggers.Add(pressed);

        var disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
        if (disabledBg is not null) disabled.Setters.Add(new Setter(Button.BackgroundProperty, disabledBg));
        if (disabledFg is not null) disabled.Setters.Add(new Setter(Button.ForegroundProperty, disabledFg));
        else disabled.Setters.Add(new Setter(Button.OpacityProperty, 0.45));
        if (disabledBg is not null && border is not null)
            disabled.Setters.Add(new Setter(Button.BorderBrushProperty, disabledBg));
        s.Triggers.Add(disabled);
        return s;
    }


    /// <summary>16-unit linear icon geometry (Lucide style, 1.5px round strokes).</summary>
    internal static class Icons
    {
        internal const string Copy =
            "M 5.5,5.5 V 4 A 2,2 0 0 1 7.5,2 H 12 A 2,2 0 0 1 14,4 V 8.5 A 2,2 0 0 1 12,10.5 H 10.5 " +
            "M 10.5,5.5 H 4 A 2,2 0 0 0 2,7.5 V 12 A 2,2 0 0 0 4,14 H 10.5 A 2,2 0 0 0 12.5,12 V 7.5 A 2,2 0 0 0 10.5,5.5 Z";
        internal const string Bolt = "M 9.5,1.5 L 3.5,9.5 H 7.5 L 6.5,14.5 L 12.5,6.5 H 8.5 Z";

        /// <summary>取景框（四角）:截图翻译,与「翻译」的闪电在形状和颜色上都区分开。</summary>
        internal const string Viewfinder =
            "M 2,6.5 V 4 A 2,2 0 0 1 4,2 H 6.5 " +
            "M 9.5,2 H 12 A 2,2 0 0 1 14,4 V 6.5 " +
            "M 14,9.5 V 12 A 2,2 0 0 1 12,14 H 9.5 " +
            "M 6.5,14 H 4 A 2,2 0 0 1 2,12 V 9.5 " +
            "M 10.4,8 A 2.4,2.4 0 1 1 5.6,8 A 2.4,2.4 0 1 1 10.4,8 Z";
    }

    internal static Path Icon(string data, Brush stroke) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = 14,
        Height = 14,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

}

/// <summary>
/// Stage-1 selection toolbar (Doubao-style): shown on selection, NO auto translate.
/// User explicitly picks 复制 or 翻译. Auto-hides on timeout / outside click / new selection.
/// </summary>
public sealed class SelectionToolbar : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _hideTimer;
    private System.Windows.DpiScale _dpi = new(1.0, 1.0);

    public event Action<ScreenPoint>? CopyClicked;
    public event Action<ScreenPoint>? TranslateClicked;
    public event Action<ScreenPoint>? ScreenshotClicked;

    public SelectionToolbar()
    {
        var copy = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    FtTheme.Icon(FtTheme.Icons.Copy, FtTheme.TextSecondary),
                    new TextBlock { Text = "复制", FontSize = 12, Foreground = FtTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) },
                },
            },
            Style = FtTheme.PillGhost(),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0),
        };
        var translate = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    FtTheme.Icon(FtTheme.Icons.Bolt, FtTheme.Brand),
                    new TextBlock { Text = "翻译", FontSize = 12, Foreground = FtTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) },
                },
            },
            Style = FtTheme.PillGhost(),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 0),
        };
        var screenshot = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    FtTheme.Icon(FtTheme.Icons.Viewfinder, FtTheme.TextSecondary),
                    new TextBlock { Text = "截图翻译", FontSize = 12, Foreground = FtTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) },
                },
            },
            Style = FtTheme.PillGhost(),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0),
        };
        copy.Click += (_, _) => Raise(CopyClicked);
        translate.Click += (_, _) => Raise(TranslateClicked);
        screenshot.Click += (_, _) => Raise(ScreenshotClicked);
        // icon+text content no longer auto-exposes a name — keep the UIA contract (E2E + scripts)
        global::System.Windows.Automation.AutomationProperties.SetName(copy, "复制");
        global::System.Windows.Automation.AutomationProperties.SetName(translate, "翻译");
        global::System.Windows.Automation.AutomationProperties.SetName(screenshot, "截图翻译");

        var divider = new Rectangle
        {
            Width = 1,
            Height = 16,
            Fill = FtTheme.Hair,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = FtTheme.UiFont,
            Content = new Border
            {
                Background = FtTheme.Surface,
                BorderBrush = FtTheme.Hair,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),

                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { copy, divider, translate, screenshot },
                },
            },
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            _dpi = VisualTreeHelper.GetDpi(_window); // cache at live time (M0: minimized windows report 96)
        };
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOverPhysical()) { _hideTimer.Stop(); _hideTimer.Start(); return; } // hover keeps it alive
            _hideTimer.Stop();
            Hide();
        };
    }

    private void Raise(Action<ScreenPoint>? handler)
    {
        var p = Anchor;
        WindowsSelectionCapture.Diag($"toolbar button clicked, handler={(handler is not null ? "wired" : "NULL")} anchor={p?.X},{p?.Y}");
        Hide();
        if (handler is not null && p.HasValue) handler(p.Value);
    }

    public ScreenPoint? Anchor { get; private set; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE) { handled = true; return (IntPtr)NativeMethods.MA_NOACTIVATE; }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Position near the selection: below its last line, above when no room (drag-upwards no
    /// longer covers the text); clamped into the monitor work area; hover pauses auto-hide.
    /// </summary>
    public void ShowAt(CapturedSelection captured)
    {
        Anchor = captured.Anchor;
        _window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wPx = _window.DesiredSize.Width * _dpi.DpiScaleX;
        var hPx = _window.DesiredSize.Height * _dpi.DpiScaleY;

        double leftPx, topPx;
        var rect = captured.Rects.Count > 0 ? captured.Rects[^1] : (ScreenRect?)null;
        if (rect is { } r && r.Height > 0 && r.Width > 0)
        {
            leftPx = Math.Min(r.X + r.Width - wPx, WorkRight() - wPx);
            topPx = r.Y + r.Height + 6;
            if (topPx + hPx > WorkBottom()) topPx = r.Y - hPx - 6; // flip above
        }
        else
        {
            leftPx = captured.Anchor.X + 8;
            topPx = captured.Anchor.Y + 14;
            if (topPx + hPx > WorkBottom()) topPx = captured.Anchor.Y - hPx - 10;
        }
        leftPx = Math.Max(WorkLeft(), Math.Min(leftPx, WorkRight() - wPx));
        topPx = Math.Max(WorkTop(), topPx);

        _window.Left = leftPx / _dpi.DpiScaleX;
        _window.Top = topPx / _dpi.DpiScaleY;
        if (!_window.IsVisible) _window.Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private static int WorkLeft() => NativeMethods.GetSystemMetrics(76);
    private static int WorkTop() => NativeMethods.GetSystemMetrics(77);
    private static int WorkRight() => WorkLeft() + NativeMethods.GetSystemMetrics(78);
    private static int WorkBottom() => WorkTop() + NativeMethods.GetSystemMetrics(79);

    public void Hide()
    {
        _hideTimer.Stop();
        if (_window.IsVisible) _window.Hide();
    }

    private bool IsMouseOverPhysical()
    {
        try
        {
            var p = System.Windows.Input.Mouse.GetPosition(_window);
            return p.X >= 0 && p.Y >= 0 && p.X <= _window.ActualWidth && p.Y <= _window.ActualHeight;
        }
        catch { return false; }
    }

    /// <summary>Physical-pixel hit test used for outside-click dismissal.</summary>
    public bool ContainsPhysical(ScreenPoint p)
    {
        if (!_window.IsVisible) return false;
        var l = _window.Left * _dpi.DpiScaleX;
        var t = _window.Top * _dpi.DpiScaleY;
        var r = l + _window.ActualWidth * _dpi.DpiScaleX;
        var b = t + _window.ActualHeight * _dpi.DpiScaleY;
        return p.X >= l && p.X <= r && p.Y >= t && p.Y <= b;
    }

    /// <summary>Create the HWND now (styles + DPI cache applied) so the first real show is instant.</summary>
    public void Prewarm() => _ = new WindowInteropHelper(_window).EnsureHandle();

    public void Dispose()
    {
        _hideTimer.Stop();
        _window.Close();
    }
}

/// <summary>
/// Stage-2 result card: loading state while translating, then the translation with
/// 复制 / 替换原文 / 插入译文 actions and an explicit ✕ close. Outside click closes.
/// </summary>
public sealed class ResultCardWindow : IDisposable
{
    private readonly Window _window;
    private readonly TextBlock _sourceBlock;
    private readonly TextBlock _targetBlock;
    private readonly TextBlock _statusBlock;
    private readonly Button _copyButton;
    private readonly Button _replaceButton;
    private readonly Button _insertButton;
    private readonly Button _closeButton;
    private readonly Ellipse _spinner;
    private readonly DockPanel _header;
    private readonly DockPanel _footer;
    private readonly RotateTransform _spinnerRotate = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private System.Windows.DpiScale _dpi = new(1.0, 1.0);
    private ScreenPoint _lastAnchor;
    private ScreenRect? _lastRect;
    private bool _hasPosition;

    public event Action? CopyRequested;
    public event Action? ReplaceRequested;
    public event Action? InsertRequested;
    public event Action? Dismissed;

    public ResultCardWindow()
    {
        _sourceBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = FtTheme.TextSecondary,
            FontSize = 12,
            MaxHeight = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _targetBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = FtTheme.TextPrimary,
            FontSize = 14,
            LineHeight = 22,
            MaxHeight = 220,
        };
        _statusBlock = new TextBlock
        {
            Foreground = FtTheme.TextMuted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _spinner = new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            Stroke = FtTheme.Brand,
            StrokeDashArray = new DoubleCollection { 34, 10 },
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 1, 8, 0),
            RenderTransform = _spinnerRotate,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        _copyButton = ActionButton("复制", FtTheme.Primary());
        _copyButton.Click += (_, _) => CopyRequested?.Invoke();
        _replaceButton = ActionButton("替换原文", FtTheme.Ghost());
        _replaceButton.Click += (_, _) => ReplaceRequested?.Invoke();
        _insertButton = ActionButton("插入译文", FtTheme.Ghost());
        _insertButton.Click += (_, _) => InsertRequested?.Invoke();
        _closeButton = new Button
        {
            Content = "✕",
            Style = FtTheme.RoundGhost(),
            FontSize = 11,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
        };
        _closeButton.Click += (_, _) => { Hide(); Dismissed?.Invoke(); };

        // header: source snippet + close
        var header = _header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_closeButton, Dock.Right);
        _closeButton.Margin = new Thickness(10, -4, -4, 0);
        header.Children.Add(_closeButton);
        header.Children.Add(_sourceBlock);

        // body: spinner + translation
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_spinner);
        Grid.SetColumn(_spinner, 0);
        body.Children.Add(_targetBlock);
        Grid.SetColumn(_targetBlock, 1);

        // footer: status + actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_insertButton);
        actions.Children.Add(_replaceButton);
        actions.Children.Add(_copyButton);
        var footer = _footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(actions);
        footer.Children.Add(_statusBlock);

        var content = new StackPanel { Children = { header, body, footer } };

        var root = new Border
        {
            Background = FtTheme.Surface,
            BorderBrush = FtTheme.HairStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            Child = content,
        };
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && _window is not null) _window.DragMove();
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.Height,
            Width = 420,
            MinWidth = 280,
            MaxWidth = 480,
            FontFamily = FtTheme.UiFont,
            Content = root,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            _dpi = VisualTreeHelper.GetDpi(_window);
        };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Hide(); };
        // a notice must also restore the buttons for the next real card
        _noticeTimer.Tick += (_, _) =>
        {
            _copyButton.IsEnabled = true;
            _copyButton.Visibility = Visibility.Visible;
        };
    }

    private static Button ActionButton(string text, Style style) => new()
    {
        Content = text,
        Style = style,
        Padding = new Thickness(13, 7, 13, 7),
        Margin = new Thickness(0, 0, 8, 0),
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE) { handled = true; return (IntPtr)NativeMethods.MA_NOACTIVATE; }
        return IntPtr.Zero;
    }

    private void StartSpinner()
    {
        _spinner.Visibility = Visibility.Visible;
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopSpinner()
    {
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _spinner.Visibility = Visibility.Collapsed;
    }

    public void ShowLoading(string previewText, CapturedSelection selection)
    {
        RestoreFullChrome();
        _sourceBlock.Text = TrimForDisplay(previewText, 120);
        _targetBlock.Text = "正在翻译…";
        _targetBlock.Foreground = FtTheme.TextSecondary;
        _statusBlock.Text = string.Empty;
        _replaceButton.Visibility = Visibility.Collapsed;
        _insertButton.Visibility = Visibility.Collapsed;
        _copyButton.IsEnabled = false;
        StartSpinner();
        ShowAt(selection.Anchor, selection.Rects.Count > 0 ? selection.Rects[0] : null);
    }

    public void ShowResult(TranslationResult result, CapturedSelection selection)
    {
        StopSpinner();
        RestoreFullChrome();
        // a failed translation must not offer 复制 for the red error text
        _copyButton.IsEnabled = result.Succeeded;
        _sourceBlock.Text = TrimForDisplay(result.RequestText, 160);
        if (result.Succeeded)
        {
            _targetBlock.Foreground = FtTheme.TextPrimary;
            _targetBlock.Text = result.TranslatedText;
            _statusBlock.Text = $"{result.Model} · {result.ElapsedMs}ms{(result.FromCache ? " · 缓存" : "")}";
            var writable = selection.IsWritable;
            _replaceButton.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
            _insertButton.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _targetBlock.Foreground = FtTheme.Danger;
            _targetBlock.Text = result.Error?.Message ?? "翻译失败";
            _statusBlock.Text = string.Empty;
            _replaceButton.Visibility = Visibility.Collapsed;
            _insertButton.Visibility = Visibility.Collapsed;
        }
        // keep current position when transitioning loading → result
        if (!_window.IsVisible) ShowAt(selection.Anchor, selection.Rects.Count > 0 ? selection.Rects[0] : null);
    }

    public void ShowAt(ScreenPoint anchor, ScreenRect? selectionRect)
    {
        // clipboard-fallback captures carry a default anchor (no rects) — without this the
        // card appeared at the top-left corner of the screen (bugbot P1)
        if (anchor.X == 0 && anchor.Y == 0 && selectionRect is null)
        {
            anchor = WindowsSelectionCapture.CurrentCursorPoint();
        }
        double x, y;
        if (selectionRect is { } r && r.Height > 0)
        {
            x = r.X / _dpi.DpiScaleX;
            y = (r.Y + r.Height + 10) / _dpi.DpiScaleY;
        }
        else
        {
            x = anchor.X / _dpi.DpiScaleX + 20;
            y = anchor.Y / _dpi.DpiScaleY + 20;
        }
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var cardW = _window.Width;
        if (x * _dpi.DpiScaleX + cardW * _dpi.DpiScaleX > vx + vw) x = (vx + vw - cardW * _dpi.DpiScaleX) / _dpi.DpiScaleX;
        if (y * _dpi.DpiScaleY + 160 * _dpi.DpiScaleY > vy + vh) y = Math.Max(vy / _dpi.DpiScaleY, y - 180 / _dpi.DpiScaleY);

        _lastAnchor = anchor;
        _lastRect = selectionRect;
        _hasPosition = true;
        _window.Left = Math.Max(0, x);
        _window.Top = Math.Max(0, y);
        if (!_window.IsVisible) _window.Show();
        _window.Topmost = true;
    }

    public bool ContainsPhysical(ScreenPoint p)
    {
        if (!_window.IsVisible) return false;
        var l = _window.Left * _dpi.DpiScaleX;
        var t = _window.Top * _dpi.DpiScaleY;
        var r = l + _window.ActualWidth * _dpi.DpiScaleX;
        var b = t + _window.ActualHeight * _dpi.DpiScaleY;
        return p.X >= l && p.X <= r && p.Y >= t && p.Y <= b;
    }

    /// <summary>Non-modal transient notice replacing every MessageBox in the tray flow —
    /// a modal dialog on the single UI thread freezes toolbar/card events app-wide.</summary>
    public void ShowNotice(string message)
    {
        StopSpinner();
        // 提示卡只留一行字：清空源片段后还把 header/footer 的外边距收掉，
        // 否则空出来的那两段间距会让卡片看起来莫名很高。
        _sourceBlock.Text = string.Empty;
        _sourceBlock.Visibility = Visibility.Collapsed;
        _header.Margin = new Thickness(0);
        _footer.Margin = new Thickness(0);
        _targetBlock.Foreground = FtTheme.Notice;
        _targetBlock.Text = message;
        _statusBlock.Text = string.Empty;
        _copyButton.IsEnabled = false;
        _copyButton.Visibility = Visibility.Collapsed;
        _replaceButton.Visibility = Visibility.Collapsed;
        _insertButton.Visibility = Visibility.Collapsed;
        // 插入失败时卡片刚在 WriteBackAsync 里被 Hide()，这里必须回到原来的划词位置；
        // 固定 (80,80) 会让提示凭空出现在屏幕左上角。
        if (!_window.IsVisible)
            ShowAt(_hasPosition ? _lastAnchor : WindowsSelectionCapture.CurrentCursorPoint(), _lastRect);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void Hide()
    {
        StopSpinner();
        if (_window.IsVisible) _window.Hide();
    }

    /// <summary>ShowNotice 会把卡片压成一行提示，这里把正常卡片的间距还原。</summary>
    private void RestoreFullChrome()
    {
        _sourceBlock.Visibility = Visibility.Visible;
        _header.Margin = new Thickness(0, 0, 0, 8);
        _footer.Margin = new Thickness(0, 14, 0, 0);
    }

    private static string TrimForDisplay(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>Create the HWND now (styles + DPI cache applied) so the first real show is instant.</summary>
    public void Prewarm() => _ = new WindowInteropHelper(_window).EnsureHandle();

    public void Dispose() => _window.Close();
}
