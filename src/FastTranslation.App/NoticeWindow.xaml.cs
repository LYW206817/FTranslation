using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 取代 MessageBox，承接所有必须让用户看到的内容（v38）。系统对话框不认调色板：它在
/// 深色应用正中糊上一块白板，而它原本只有两个用途——「阻塞到用户确认」和「问一句是/否」——
/// 这两件事在这里都能做，且没有那个代价。
/// </summary>
public partial class NoticeWindow : Window
{
    private readonly string? _linkUrl;
    private readonly bool _confirm;

    private NoticeWindow(string title, string body, string? linkUrl, string? linkLabel, bool confirm)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _linkUrl = linkUrl;
        _confirm = confirm;

        // 窗口标题同时充当大标题，和其他所有窗口的做法一致
        Title = title;
        Headline.Text = title;
        BodyText.Text = body;

        if (!string.IsNullOrEmpty(linkUrl))
        {
            LinkButton.Content = string.IsNullOrEmpty(linkLabel) ? "打开下载页" : linkLabel;
            LinkRow.Visibility = Visibility.Visible;
        }

        if (confirm)
        {
            // 问句需要有个说「不」的出口；主按钮承载肯定回答
            CancelAction.Visibility = Visibility.Visible;
            PrimaryAction.Content = "继续";
        }
    }

    /// <summary>
    /// 阻塞到用户关闭对话框。传入 linkUrl 会多出一个按钮，在浏览器里打开它；
    /// 没有可打开的内容时传 null。
    /// </summary>
    public static void Show(Window? owner, string title, string body, string? linkUrl = null, string? linkLabel = null)
    {
        var window = new NoticeWindow(title, body, linkUrl, linkLabel, confirm: false);
        window.ShowFor(owner);
    }

    /// <summary>阻塞到用户作答。用户点了肯定按钮时返回 true。</summary>
    public static bool Confirm(Window? owner, string title, string body)
    {
        var window = new NoticeWindow(title, body, null, null, confirm: true);
        return window.ShowFor(owner) == true;
    }

    private bool? ShowFor(Window? owner)
    {
        // 没有 Owner 的 CenterOwner 窗口会落在任意位置；更新器经常在应用缩在托盘、
        // 没有任何窗口可以充当 Owner 时运行
        if (owner is { IsVisible: true })
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return ShowDialog();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_linkUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _linkUrl, UseShellExecute = true });
        }
        catch
        {
            // URL 在正文里也显示着；浏览器打不开在这里不算致命
            Clipboard.SetText(_linkUrl);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_confirm)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
