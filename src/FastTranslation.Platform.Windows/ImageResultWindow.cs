using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 译图浮窗。图片翻译不在原位置覆盖译文（用户决策），译后图单独开一个可拖动的窗口展示：
/// 复制图片 / 另存为 / 关闭。窗口只建一次，换图复用（每次截屏都新建 HWND 会拖慢第二次以后的开窗）。
/// </summary>
public sealed class ImageResultWindow : IDisposable
{
    /// <summary>图片最多占工作区的这个比例：再大就会盖住用户正要对照的原文。</summary>
    private const double MaxWorkAreaFraction = 0.7;

    private readonly Window _window;
    private readonly TextBlock _caption;
    private readonly Image _image;

    private byte[]? _jpegBytes;

    public ImageResultWindow()
    {
        _caption = new TextBlock
        {
            Foreground = FtTheme.TextPrimary,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _image = new Image { Stretch = Stretch.Uniform };

        var copy = Pill("复制图片", FtTheme.Primary());
        copy.Click += (_, _) => CopyImage();
        var save = Pill("另存为…", FtTheme.Ghost());
        save.Click += (_, _) => SaveImage();
        var close = Pill("关闭", FtTheme.PillGhost());
        close.Click += (_, _) => Hide();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(copy);
        actions.Children.Add(save);
        actions.Children.Add(close);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(_caption);

        var root = new Border
        {
            Background = FtTheme.Surface,
            BorderBrush = FtTheme.Hair,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 14),
            Effect = FtTheme.CardShadow(),
            Child = new StackPanel { Children = { header, _image } },
        };
        // 无边框窗口没有标题栏可抓：标题条和图片区的空白处都用来拖动
        root.MouseLeftButtonDown += (_, e) =>
        {
            // 这个 lambda 在构造完成前就可能被调用，所以 _window 要判空（同 ResultCardWindow）
            if (e.ButtonState == MouseButtonState.Pressed && _window is not null) _window.DragMove();
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = FtTheme.UiFont,
            Content = root,
        };
        // 浮窗是唯一需要键盘的窗口（另存为对话框 / Esc），所以不像工具栏那样用 WS_EX_NOACTIVATE
        _window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Hide();
        };
    }

    /// <summary>展示一张译后图（JPG 字节）。同一个窗口反复调用即可换图。</summary>
    public void ShowResult(byte[] imageBytes, string caption)
    {
        if (imageBytes is null || imageBytes.Length == 0) return;
        var bitmap = Decode(imageBytes);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;

        _jpegBytes = imageBytes;
        _caption.Text = caption;
        _image.Source = bitmap;

        // 小图不放大：DIP 尺寸按图片自己的 DPI 换算（bitmap.Width 已经是 DIP），
        // 只有超过工作区上限才等比缩小。
        var work = SystemParameters.WorkArea;
        var scale = Math.Min(1.0, Math.Min(
            work.Width * MaxWorkAreaFraction / bitmap.Width,
            work.Height * MaxWorkAreaFraction / bitmap.Height));
        _image.Width = bitmap.Width * scale;
        _image.Height = bitmap.Height * scale;

        if (!_window.IsVisible) _window.Show();
        _window.Topmost = true;
        _window.Activate(); // Esc 和另存为对话框都要焦点
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
        if (_window.IsVisible) _window.Hide();
    }

    public void Dispose() => _window.Close();
}
