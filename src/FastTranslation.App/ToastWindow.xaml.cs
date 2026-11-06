using System.Windows;
using System.Windows.Threading;

namespace FastTranslation.App;

/// <summary>
/// 右下角的非模态提示条：一句话反馈，可带一个动作按钮，几秒后自己消失。
/// 用于「注册成功 / 登录成功 / 还没登录」这类不值得打断用户、又必须有回应的反馈。
/// </summary>
public sealed partial class ToastWindow : Window
{
    private const int AutoHideMs = 3_000;

    /// <summary>带按钮时要多留一会，否则来不及点就走。</summary>
    private const int WithActionHoldMs = 6_000;

    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;
    private Action? _onAction;

    private ToastWindow(string message, string? actionLabel, Action? onAction)
    {
        InitializeComponent();
        MessageText.Text = message;
        var hasAction = !string.IsNullOrEmpty(actionLabel);
        if (hasAction)
        {
            ActionButton.Content = actionLabel;
            ActionButton.Visibility = Visibility.Visible;
            _onAction = onAction;
        }
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(hasAction ? WithActionHoldMs : AutoHideMs)
        };
        _timer.Tick += (_, _) => Close();
    }

    /// <summary>弹出一条提示；上一条还没消失会被替换掉，同一时刻只留一条。</summary>
    /// <param name="message">正文，一到两行。</param>
    /// <param name="actionLabel">动作按钮文字；为空表示纯提示。</param>
    /// <param name="onAction">点按钮时执行；给了 <paramref name="actionLabel"/> 就应该给。</param>
    public static void Show(string message, string? actionLabel = null, Action? onAction = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _current?.Close();
        var toast = new ToastWindow(message, actionLabel, onAction);
        _current = toast;
        // 必须等布局完成：SizeToContent 的窗口在 Show 之前 ActualHeight 还是 0
        toast.ContentRendered += (_, _) => toast.PlaceAtBottomRight();
        toast.Show();
    }

    private void PlaceAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    // 先关窗再执行动作：动作里可能弹出别的窗口，让它落在提示条已经腾开的位置上
    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var action = _onAction;
        Close();
        action?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }
}
