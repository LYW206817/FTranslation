using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShapesPath = System.Windows.Shapes.Path;

namespace FastTranslation.Platform.Windows;

/// <summary>译图窗保存笔记时要带上的一句译文及其来源信息（原文由窗口自己给，因为用户可以改）。</summary>
public sealed record ImageNoteContext(string TargetText, string SourceLang, string TargetLang, string Model)
{
    public static readonly ImageNoteContext Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>点「保存到单词本」时带出的载荷：原文取识别框里当前的内容，译文只在原文没被改过时才带。</summary>
public sealed record ImageNoteDraft(string SourceText, string TargetText, string SourceLang, string TargetLang, string Model)
{
    /// <summary>
    /// 用户改了识别文本后语义可能分叉，此时宁可留空也不要存一句对不上的译文；
    /// 模型名跟着译文一起丢，否则单词本会显示「这句是 qwen-mt-flash 翻的」而其实没有。
    /// </summary>
    public static ImageNoteDraft Create(string? originalText, string? editedText, ImageNoteContext? context)
    {
        var source = editedText?.Trim() ?? string.Empty;
        var note = context ?? ImageNoteContext.Empty;
        var keepTranslation = note.TargetText.Trim().Length > 0
            && string.Equals(source, originalText?.Trim(), StringComparison.Ordinal);
        return new ImageNoteDraft(
            source,
            keepTranslation ? note.TargetText.Trim() : string.Empty,
            note.SourceLang,
            note.TargetLang,
            keepTranslation ? note.Model : string.Empty);
    }
}

/// <summary>
/// 译图浮窗。图片翻译不在原位置覆盖译文（用户决策），译后图单独开一个可拖动的窗口展示：
/// 复制图片 / 另存为 / 关闭。窗口只建一次，换图复用（每次截屏都新建 HWND 会拖慢第二次以后的开窗）。
/// v63：尺寸从「跟着图片缩放」改成固定大小 + 可拖拽边角调整，图在窗内等比缩放，
/// 这样窄长条截图不会再缩成一条缝，用户想放大也能自己拉。
/// </summary>
public sealed class ImageResultWindow : IDisposable
{
    /// <summary>首次打开的目标尺寸。小屏幕上由 <see cref="SizeToDefault"/> 收窄。</summary>
    private const double DefaultWidth = 780;
    private const double DefaultHeight = 620;

    /// <summary>识别文本区在窗口里的固定高度：它总是要能改，但不能把图片挤没。</summary>
    private const double RecognizedHeight = 132;

    private readonly Window _window;
    private readonly TextBlock _caption;
    private readonly Image _image;
    private readonly Grid _imageHost;
    private readonly StackPanel _loadingPanel;
    private readonly Ellipse _spinner;
    private readonly RotateTransform _spinnerRotate = new();
    private readonly TextBlock _recognizedLabel;
    private readonly TextBox _recognized;
    private readonly Button _copy;
    private readonly Button _saveAs;
    private readonly Button _saveNote;
    private readonly RowDefinition _imageRow;
    private readonly RowDefinition _recognizedRow;
    private readonly TextBlock _noteStatus;

    private byte[]? _jpegBytes;
    private ImageNoteContext _noteContext = ImageNoteContext.Empty;
    private string _recognizedOriginal = string.Empty;

    /// <summary>点了「保存到单词本」。原文取识别框当前内容，译文是否作数由 <see cref="ImageNoteDraft.Create"/> 判断。</summary>
    public event Action<ImageNoteDraft>? SaveRequested;

    public ImageResultWindow()
    {
        _caption = new TextBlock
        {
            Foreground = FtTheme.TextPrimary,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _image = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };

        _spinner = new Ellipse
        {
            Width = 18,
            Height = 18,
            StrokeThickness = 2,
            Stroke = FtTheme.Brand,
            // 虚线单位是线宽的倍数：{34,10} 等于「68 虚线 + 20 空隙」，而 18px 圆的周长只有 56，
            // 整圈画满没有空隙，转起来看像素几乎不变（实测四帧只差 4 个像素）。改成三段短弧才看得见转。
            StrokeDashArray = new DoubleCollection { 6, 2.5 },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            RenderTransform = _spinnerRotate,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed,
        };
        var loadingText = new TextBlock
        {
            Text = "正在快马加鞭翻译~",
            Foreground = FtTheme.TextSecondary,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _loadingPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _loadingPanel.Children.Add(_spinner);
        _loadingPanel.Children.Add(loadingText);

        // 转圈和图片同一格：等待时占位、出图时互换，窗口尺寸不变，不会先缩后张
        _imageHost = new Grid();
        _imageHost.Children.Add(_image);
        _imageHost.Children.Add(_loadingPanel);

        _copy = Pill("复制图片", FtTheme.Primary());
        _copy.Click += (_, _) => CopyImage();
        _saveAs = Pill("另存为…", FtTheme.Ghost());
        _saveAs.Click += (_, _) => SaveImage();

        _recognizedLabel = new TextBlock
        {
            // OCR 偶尔把 l/1、O/0 认错，而这条文本会直接进单词本，所以保存前必须能修
            Text = "识别到的原文（可修改后保存）",
            Foreground = FtTheme.TextSecondary,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _recognized = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = RecognizedHeight,
            MaxHeight = RecognizedHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 6, 4),
        };

        _saveNote = Pill("保存到单词本", FtTheme.Ghost());
        _saveNote.Click += (_, _) => RequestSave();
        var close = Pill("关闭", FtTheme.PillGhost());
        close.Click += (_, _) => Hide();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_copy);
        actions.Children.Add(_saveAs);
        actions.Children.Add(_saveNote);
        actions.Children.Add(close);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(_caption);

        // 保存结果只在窗内给一句：托盘气泡会跟着别的通知排队，用户盯着窗口时看不到
        _noteStatus = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var labelRow = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
        DockPanel.SetDock(_noteStatus, Dock.Right);
        labelRow.Children.Add(_noteStatus);
        labelRow.Children.Add(_recognizedLabel);

        _imageRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        _recognizedRow = new RowDefinition { Height = GridLength.Auto };
        var content = new Grid { Margin = new Thickness(14, 12, 14, 14) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(_imageRow);
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(_recognizedRow);
        content.Children.Add(header);
        content.Children.Add(_imageHost);
        Grid.SetRow(_imageHost, 1);
        content.Children.Add(labelRow);
        Grid.SetRow(labelRow, 2);
        content.Children.Add(_recognized);
        Grid.SetRow(_recognized, 3);

        var outer = new Grid();
        outer.Children.Add(content);
        AddResizeGrips(outer);

        var root = new Border
        {
            Background = FtTheme.Surface,
            BorderBrush = FtTheme.Hair,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = outer,
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Topmost = true,
            // 固定尺寸模式：图片等比缩放在自己的格子里，用户拖边角再改
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = FtTheme.UiFont,
            MinWidth = 520,
            MinHeight = 360,
            Content = root,
        };
        SizeToDefault();

        // 无边框窗口没有标题栏可抓：标题条和图片区的空白都用来拖动。
        // 缩放手柄自己会吃掉鼠标事件，这里的兜底判断防止它冒泡上来时变成移动窗口。
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.OriginalSource is DependencyObject source && FindAncestor<Thumb>(source) is not null) return;
            _window.DragMove();
        };
        // 浮窗是唯一需要键盘的窗口（另存为对话框 / Esc），所以不像工具栏那样用 WS_EX_NOACTIVATE
        _window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Hide();
        };
    }

    /// <summary>只有图片、没有识别文本时的旧入口。</summary>
    public void ShowResult(byte[] imageBytes, string caption) =>
        ShowResult(imageBytes, caption, null, true);

    /// <summary>
    /// 等待态：框选结束后立刻开窗，别让用户对着空屏猜程序是不是卡了。
    /// 结果回来时由 <see cref="ShowResult"/> 原地替换内容，窗口尺寸不变。
    /// </summary>
    public void ShowLoading(string caption)
    {
        _caption.Text = caption;
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _jpegBytes = null;
        _loadingPanel.Visibility = Visibility.Visible;
        StartSpinner();

        // 等待时只留「关闭」：其他按钮都还没有可作用的对象
        _copy.Visibility = Visibility.Collapsed;
        _saveAs.Visibility = Visibility.Collapsed;
        _saveNote.Visibility = Visibility.Collapsed;
        _saveNote.IsEnabled = false;
        _recognizedLabel.Visibility = Visibility.Collapsed;
        _noteStatus.Visibility = Visibility.Collapsed;
        _recognized.Visibility = Visibility.Collapsed;
        _recognized.Text = string.Empty;
        _imageRow.Height = new GridLength(1, GridUnitType.Star);
        _recognizedRow.Height = GridLength.Auto;

        Present();
    }

    /// <summary>
    /// 展示一次截图翻译的结果。<paramref name="recognized"/> 为 null 表示 OCR 没成功，
    /// 此时隐藏文本区并禁用「保存到单词本」——存一条只有图没有原文的笔记没有意义。
    /// <paramref name="showActions"/> 为 false 时（图片翻译失败、只有文字）隐藏图片类操作。
    /// <param name="note">OCR 文本的译文，用户点保存时一起写进单词本；没有译文传 null。
    /// </summary>
    public void ShowResult(byte[]? imageBytes, string caption, string? recognized, bool showActions, ImageNoteContext? note = null)
    {
        _caption.Text = caption;
        StopSpinner();

        _copy.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        _saveAs.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        _saveNote.Visibility = Visibility.Visible;
        _noteContext = note ?? ImageNoteContext.Empty;
        // 每次都是新的截图：上一张的「已保存」不能留到这一张
        ResetSaveState();

        if (recognized is null)
        {
            _recognizedLabel.Visibility = Visibility.Collapsed;
            _recognized.Visibility = Visibility.Collapsed;
            _recognized.Text = string.Empty;
            _recognizedOriginal = string.Empty;
            _saveNote.IsEnabled = false;
        }
        else
        {
            _recognizedLabel.Visibility = Visibility.Visible;
            _recognized.Visibility = Visibility.Visible;
            _recognized.Text = recognized;
            _recognizedOriginal = recognized.Trim();
            _saveNote.IsEnabled = true;
        }

        if (imageBytes is { Length: > 0 } && Decode(imageBytes) is { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
        {
            _jpegBytes = imageBytes;
            _image.Source = bitmap;
            _image.Visibility = Visibility.Visible;
            _loadingPanel.Visibility = Visibility.Collapsed;
            // 图占满剩余空间，文本区保持固定高度；窗口尺寸不再随图片变化
            _imageRow.Height = new GridLength(1, GridUnitType.Star);
            _recognizedRow.Height = recognized is null ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            _recognized.Height = recognized is null ? double.NaN : RecognizedHeight;
            _recognized.MaxHeight = recognized is null ? double.PositiveInfinity : RecognizedHeight;
        }
        else
        {
            // 图片翻译失败但 OCR 成功：窗口仍要开，只是没有图可看，文本区接管整块空间。
            _jpegBytes = null;
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            _loadingPanel.Visibility = Visibility.Collapsed;
            _imageRow.Height = new GridLength(0);
            _recognizedRow.Height = new GridLength(1, GridUnitType.Star);
            _recognized.Height = double.NaN;
            _recognized.MaxHeight = double.PositiveInfinity;
        }

        Present();
    }

    /// <summary>保存成功的窗内回执，样式照抄结果卡的 MarkSaved。</summary>
    public void MarkSaved(string kind)
    {
        _saveNote.Content = "已保存";
        _saveNote.IsEnabled = false;
        ShowSaveStatus("已保存到" + kind + "本", FtTheme.TextSecondary);
    }

    /// <summary>保存失败（未登录、额度用尽、网络）时的窗内提示，可重试所以按钮保持可用。</summary>
    public void ShowSaveError(string message) => ShowSaveStatus(message, FtTheme.Danger);

    private void ShowSaveStatus(string text, Brush brush)
    {
        _noteStatus.Text = text;
        _noteStatus.Foreground = brush;
        _noteStatus.Visibility = Visibility.Visible;
    }

    private void ResetSaveState()
    {
        _saveNote.Content = "保存到单词本";
        _saveNote.IsEnabled = true;
        _noteStatus.Text = string.Empty;
        _noteStatus.Visibility = Visibility.Collapsed;
    }

    private void RequestSave()
    {
        var draft = ImageNoteDraft.Create(_recognizedOriginal, _recognized.Text, _noteContext);
        if (draft.SourceText.Length > 0) SaveRequested?.Invoke(draft);
    }

    private void Present()
    {
        if (!_window.IsVisible)
        {
            KeepInsideWorkArea();
            _window.Show();
        }
        _window.Topmost = true;
        _window.Activate(); // Esc 和另存为对话框都要焦点
    }

    /// <summary>窗口被拖到副屏或分辨率变化后，重新开窗时别落到看不见的地方。</summary>
    private void KeepInsideWorkArea()
    {
        var work = SystemParameters.WorkArea;
        _window.Left = Math.Max(work.Left, Math.Min(_window.Left, work.Right - _window.Width));
        _window.Top = Math.Max(work.Top, Math.Min(_window.Top, work.Bottom - _window.Height));
    }

    private void SizeToDefault()
    {
        var work = SystemParameters.WorkArea;
        // 固定尺寸在 1366x768 这类屏上会超出工作区，收窄到九折以内
        _window.Width = Math.Min(DefaultWidth, work.Width * 0.9);
        _window.Height = Math.Min(DefaultHeight, work.Height * 0.9);
    }

    private void StartSpinner()
    {
        _spinner.Visibility = Visibility.Visible;
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopSpinner()
    {
        // 不停下来会在窗口隐藏后继续跑动画，白白占一个渲染线程
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _spinner.Visibility = Visibility.Collapsed;
        _loadingPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 右边、下边、右下角三个缩放手柄。AllowsTransparency 的无边框窗口拿不到系统的
    /// 缩放边框（窗口矩形里全是透明像素，命中测试落不到真边框上），所以自己用 Thumb 实现。
    /// </summary>
    private void AddResizeGrips(Grid outer)
    {
        const double Edge = 6;
        const double Corner = 18;

        var right = Grip(Cursors.SizeWE, HorizontalAlignment.Right, VerticalAlignment.Stretch, Edge, double.NaN);
        right.DragDelta += (_, e) => ResizeBy(e.HorizontalChange, 0);
        var bottom = Grip(Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, Edge);
        bottom.DragDelta += (_, e) => ResizeBy(0, e.VerticalChange);
        var corner = Grip(Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, Corner, Corner);
        corner.DragDelta += (_, e) => ResizeBy(e.HorizontalChange, e.VerticalChange);

        // 右下角画两道斜杠，否则用户看不出这里能拉
        var glyph = new ShapesPath
        {
            Data = Geometry.Parse("M 0,10 L 10,0 M 5,15 L 15,5"),
            Stroke = FtTheme.TextMuted,
            StrokeThickness = 1,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            IsHitTestVisible = false,
        };

        outer.Children.Add(right);
        outer.Children.Add(bottom);
        outer.Children.Add(corner);
        outer.Children.Add(glyph);
    }

    private static Thumb Grip(Cursor cursor, HorizontalAlignment h, VerticalAlignment v, double width, double height) => new()
    {
        Cursor = cursor,
        HorizontalAlignment = h,
        VerticalAlignment = v,
        Width = width,
        Height = height,
        Background = Brushes.Transparent,
    };

    private void ResizeBy(double dx, double dy)
    {
        _window.Width = Math.Max(_window.MinWidth, _window.Width + dx);
        _window.Height = Math.Max(_window.MinHeight, _window.Height + dy);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static Button Pill(string text, Style style) => new()
    {
        Content = text,
        Style = style,
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(8, 0, 0, 0),
    };

    /// <summary>byte[] → 冻结的 BitmapSource（不用 System.Drawing，本仓没有那个引用）。</summary>
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        // OnLoad：流在 EndInit 返回后立刻销毁，所以必须当场把像素读进来
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze(); // 冻结后可以跨线程复用，也不会被后续布局改动
        return bitmap;
    }

    private void CopyImage()
    {
        if (_image.Source is not BitmapSource bitmap) return;
        try { Clipboard.SetImage(bitmap); } catch { } // 剪贴板被别的进程锁住时不能把窗口带崩
    }

    private void SaveImage()
    {
        if (_jpegBytes is not { Length: > 0 } bytes) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"翻译结果-{DateTime.Now:yyyyMMdd-HHmmss}.jpg",
            DefaultExt = ".jpg",
            Filter = "JPEG 图片|*.jpg",
        };
        // 指定 owner：浮窗是 Topmost，无主对话框可能被压到它后面
        if (dialog.ShowDialog(_window) != true) return;
        try { File.WriteAllBytes(dialog.FileName, bytes); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"save image failed: {ex.Message}"); }
    }

    public void Hide()
    {
        if (!_window.IsVisible) return;
        StopSpinner();
        _window.Hide();
    }

    public void Dispose() => _window.Close();
}
