using System.Windows;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 未登录时的独立登录/注册弹窗：启动或退出登录后先拦在这里，不露出主窗口各功能页；
/// 登录或注册成功后再关闭本窗并打开主窗口首页。用户主动关掉本窗（点 X）视为取消进入应用。
/// </summary>
public sealed partial class AuthWindow : Window
{
    private bool _closingProgrammatically;

    /// <summary>用户未登录就关闭了弹窗时触发，通知宿主收尾退出。</summary>
    public event Action? Cancelled;

    public AuthWindow(AccountCoordinator account, AuthPanelMode initialMode = AuthPanelMode.Login)
    {
        InitializeComponent();
        WindowChrome.Apply(this);

        // 小屏 / 高 DPI 笔记本（如 1920x1080@150%）工作区高约 672 DIP，限制最大宽高避免被任务栏遮住。
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(400, work.Width - 24));
        MaxHeight = Math.Max(420, work.Height - 24);

        AuthForm.Initialize(account);
        AuthForm.SetStandaloneDialog(true);
        AuthForm.SetDefaultSubmit(true);
        AuthForm.SetMode(initialMode);

        Loaded += (_, _) => AuthForm.FocusInitial();
        // 切到「重置密码」时窗体会按内容增高：若底部探出工作区，顺势上移保住操作按钮。
        SizeChanged += (_, _) => ClampToWorkArea();
    }

    internal void SetMode(AuthPanelMode mode) => AuthForm.SetMode(mode);

    /// <summary>登录成功进入主界面或退出进程时的程序性关闭，不触发 <see cref="Cancelled"/>。</summary>
    public void CloseProgrammatically()
    {
        _closingProgrammatically = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closingProgrammatically) Cancelled?.Invoke();
    }

    private void ClampToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (Top + ActualHeight > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - ActualHeight);
        }
    }
}
