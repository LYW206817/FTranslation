using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core.Account;

namespace FastTranslation.App;

/// <summary>登录弹窗与账号页共用的表单模式。登录 / 注册 / 重置密码共用同一套邮箱与验证码校验。</summary>
public enum AuthPanelMode
{
    Login,
    Register,
    Reset,
}

/// <summary>
/// 未登录表单：独立登录弹窗（<see cref="AuthWindow"/>）与账号页共复用这一个控件，
/// 避免注册验证码开关、冷却倒计时和错误文案在两处分叉。
/// </summary>
public sealed partial class AuthPanel : UserControl
{
    private AccountCoordinator? _account;
    private AuthPanelMode _mode = AuthPanelMode.Login;
    private bool _busy;
    private bool _codeCooldown;
    private bool _verificationRequired = true;
    private bool _suppressTabEvents;
    private readonly bool _initialized;

    /// <summary>登录或注册成功后触发（此时会话已写入 <see cref="AccountCoordinator"/>）。</summary>
    public event Action? Authenticated;

    public AuthPanel()
    {
        InitializeComponent();
        _initialized = true;
        ApplyMode();
        UpdateWatermarks();
    }

    /// <summary>注入会话协调器；使用前必须调用一次。</summary>
    public void Initialize(AccountCoordinator account) => _account = account;

    /// <summary>
    /// 独立弹窗铺满窗口消除「窗内套卡」的双层边框；嵌在账号页时保留卡片描边、阴影并居中限宽。
    /// </summary>
    public void SetStandaloneDialog(bool standalone)
    {
        if (standalone)
        {
            CardShell.BorderThickness = new Thickness(0);
            CardShell.CornerRadius = new CornerRadius(0);
            CardShell.Effect = null;
            CardShell.Width = double.NaN;
            CardShell.HorizontalAlignment = HorizontalAlignment.Stretch;
            AppHeader.Visibility = Visibility.Visible;
        }
        else
        {
            CardShell.Style = (Style)Application.Current.FindResource("Card");
            CardShell.Padding = new Thickness(0);
            CardShell.Margin = new Thickness(0);
            CardShell.Width = 460;
            CardShell.HorizontalAlignment = HorizontalAlignment.Center;
            AppHeader.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>在弹窗宿主里把主操作设为回车默认按钮，方便输完密码直接按 Enter 提交。</summary>
    public void SetDefaultSubmit(bool isDefault) => SubmitButton.IsDefault = isDefault;

    public void SetMode(AuthPanelMode mode)
    {
        _mode = mode;
        ApplyMode();
        SetError(null);
    }

    public void ResetToLogin()
    {
        _mode = AuthPanelMode.Login;
        ApplyMode();
        PasswordBox.Clear();
        ResetConfirmPasswordBox.Clear();
        CodeBox.Clear();
        SetError(null);
        UpdateWatermarks();
    }

    /// <summary>弹窗首次显示时把键盘焦点放到邮箱框，用户打开就能直接输入。</summary>
    public void FocusInitial()
    {
        EmailBox.Focus();
        Keyboard.Focus(EmailBox);
    }

    private void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressTabEvents || _busy) return;
        _mode = ReferenceEquals(sender, RegisterTab) ? AuthPanelMode.Register : AuthPanelMode.Login;
        ApplyMode();
        SetError(null);
    }

    private void BackToLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _mode = AuthPanelMode.Login;
        ApplyMode();
        SetError(null);
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _mode = AuthPanelMode.Reset;
        ApplyMode();
        SetError(null);
    }

    /// <summary>登录/注册/重置三套文案、分段标签与验证码行只在这里切换，退出登录时也必须复位。</summary>
    private void ApplyMode()
    {
        var reset = _mode == AuthPanelMode.Reset;
        var register = _mode == AuthPanelMode.Register;

        _suppressTabEvents = true;
        try
        {
            LoginTab.IsChecked = _mode == AuthPanelMode.Login;
            RegisterTab.IsChecked = register;
        }
        finally
        {
            _suppressTabEvents = false;
        }

        ModeTabsBar.Visibility = reset ? Visibility.Collapsed : Visibility.Visible;
        ResetNavRow.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;

        FormTitle.Text = reset ? "重置密码" : register ? "创建你的账号" : "登录 Fast Translation";
        ModeHint.Text = reset
            ? "输入注册邮箱，收到验证码后设置新密码。"
            : register
                ? "注册后自动登录，免费额度立刻可用。"
                : "登录后使用划词、截图与云端单词本。";
        PasswordLabelText.Text = reset ? "新密码" : "密码";
        PasswordPlaceholder.Text = _mode == AuthPanelMode.Login ? "输入密码" : "至少 8 位密码";
        ForgotPasswordButton.Visibility = _mode == AuthPanelMode.Login ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Visibility = _mode == AuthPanelMode.Login ? Visibility.Collapsed : Visibility.Visible;
        ConfirmPasswordRow.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
        ResetHint.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
        CodeRow.Visibility = reset || (register && _verificationRequired)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubmitButton.Content = reset ? "重置密码" : register ? "注册并登录" : "登录";
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) return;
        var email = EmailBox.Text.Trim();
        if (email.Length == 0) { SetError("请先填写邮箱。"); return; }
        if (!EmailFormat.IsValid(email)) { SetError("邮箱格式不对，检查一下有没有输错。"); return; }

        _codeCooldown = true;
        SendCodeButton.IsEnabled = false;
        try
        {
            if (_mode == AuthPanelMode.Reset)
            {
                var reset = await _account.RequestPasswordResetCodeAsync(email, CancellationToken.None);
                if (!reset.Succeeded)
                {
                    SetError(reset.Error!.Display);
                    return;
                }
                // 服务端不区分邮箱是否存在，界面也必须保持同样的口径。
                CodeHint.Text = "如果该邮箱已注册，验证码已发送，10 分钟内有效。";
                SetError(null);
                return;
            }

            var result = await _account.RequestCodeAsync(email, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetError(result.Error!.Display);
                return;
            }
            if (!result.Value!.VerificationRequired)
            {
                // 这个服务端不校验邮箱：别再去要一个永远不会到的验证码
                _verificationRequired = false;
                CodeRow.Visibility = Visibility.Collapsed;
                SetError(null);
                CodeHint.Text = "这个服务器不需要邮箱验证码，直接注册即可。";
                return;
            }
            CodeHint.Text = "验证码已发送，10 分钟内有效。";
            SetError(null);
        }
        catch (Exception)
        {
            // async void 入口必须自己兜住：全局处理器只记 Debug，用户会看到按钮转一圈后毫无反馈。
            SetError("发送验证码失败，请稍后再试。");
        }
        finally
        {
            // 重发在服务端也有限流；本地冷却只是避免点下去没反应
            await Task.Delay(TimeSpan.FromSeconds(60));
            _codeCooldown = false;
            SendCodeButton.IsEnabled = !_busy;
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _account is null) return;
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (email.Length == 0) { SetError("请填写邮箱。"); return; }
        // 登录也先查格式:服务端登录只查不校验,填错格式只会得到含糊的「邮箱或密码不正确」。
        if (!EmailFormat.IsValid(email)) { SetError("邮箱格式不对，检查一下有没有输错。"); return; }
        if (password.Length == 0) { SetError(_mode == AuthPanelMode.Reset ? "请填写新密码。" : "请填写密码。"); return; }

        SetBusy(true);
        try
        {
            if (_mode == AuthPanelMode.Reset)
            {
                var code = CodeBox.Text.Trim();
                if (code.Length == 0) { SetError("请填写邮箱验证码。"); return; }
                if (password.Length < 8) { SetError("新密码至少 8 位。"); return; }
                if (!string.Equals(password, ResetConfirmPasswordBox.Password, StringComparison.Ordinal))
                {
                    SetError("两次输入的新密码不一样。");
                    return;
                }

                var reset = await _account.ResetPasswordAsync(email, code, password, CancellationToken.None);
                if (!reset.Succeeded) { SetError(reset.Error!.Display); return; }

                PasswordBox.Clear();
                ResetConfirmPasswordBox.Clear();
                CodeBox.Clear();
                _mode = AuthPanelMode.Login;
                ApplyMode();
                SetError(null);
                UpdateWatermarks();
                ToastWindow.Show("密码已重置，请用新密码登录。");
                return;
            }

            if (_mode == AuthPanelMode.Register)
            {
                var code = _verificationRequired ? CodeBox.Text.Trim() : null;
                var result = await _account.RegisterAsync(email, password, code, CancellationToken.None);
                if (!result.Succeeded) { SetError(RegisterError(result.Error!)); return; }
                PasswordBox.Clear();
                CodeBox.Clear();
                SetError(null);
                UpdateWatermarks();
                // 弹窗/面板随后就会收起或切走，先给明确的非模态反馈。
                ToastWindow.Show("注册成功，已自动登录，免费额度立刻可用。");
                Authenticated?.Invoke();
                return;
            }

            var signIn = await _account.SignInAsync(email, password, CancellationToken.None);
            if (!signIn.Succeeded) { SetError(signIn.Error!.Display); return; }
            PasswordBox.Clear();
            SetError(null);
            UpdateWatermarks();
            var profile = _account.Session?.Profile;
            ToastWindow.Show(profile is null
                ? "登录成功。"
                : $"登录成功：{profile.Email}（{profile.PlanDisplay}）");
            Authenticated?.Invoke();
        }
        catch (Exception)
        {
            SetError("操作没完成，请稍后再试。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string RegisterError(AccountError error) => error.Code switch
    {
        AccountErrorCode.Verification => "验证码不正确或已过期，点「获取验证码」重新发送。",
        _ => error.Display,
    };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        LoginTab.IsEnabled = !busy;
        RegisterTab.IsEnabled = !busy;
        BackToLoginButton.IsEnabled = !busy;
        SendCodeButton.IsEnabled = !busy && !_codeCooldown;
        ForgotPasswordButton.IsEnabled = !busy;
        SubmitButton.Content = busy
            ? "请稍候…"
            : _mode switch
            {
                AuthPanelMode.Register => "注册并登录",
                AuthPanelMode.Reset => "重置密码",
                _ => "登录",
            };
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorBanner.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWatermarks()
    {
        EmailPlaceholder.Visibility = EmailBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordPlaceholder.Visibility = PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPasswordPlaceholder.Visibility = ResetConfirmPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CodePlaceholder.Visibility = CodeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateWatermarks();
        SetError(null);
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        UpdateWatermarks();
        SetError(null);
    }
}
