using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// 账号页：你是谁，以及围绕这件事能改的三样东西（名字、密码、退出登录）。登录也在这里，
/// 因为大家找「账号」就是找这里。
///
/// <para>
/// 购买属于套餐页；本页只展示你当前拥有的内容并链过去，这样两个页面不会都自称
/// 「账号界面」。
/// </para>
/// </summary>
public partial class AccountPage : UserControl
{
    private readonly AccountCoordinator _account;
    private bool _registerMode;
    private bool _busy;
    private bool _verificationRequired = true;

    /// <summary>用户要求看价目表时触发；导航由外壳负责。</summary>
    public event Action? PlanRequested;

    public AccountPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        _account.Changed += OnAccountChanged;
        Unloaded += (_, _) => _account.Changed -= OnAccountChanged;
        Render();
    }

    /// <summary>每次访问都由宿主调用：页面是缓存的，否则额度会过期不更新。</summary>
    public void Reload()
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    }

    // 会话可能在本页第一次打开之后才到（启动恢复是异步的），而那时 Reload 已经跑过了，
    // 所以迟到的账号变更还要再问一次用量
    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    });

    private void Render()
    {
        var session = _account.Session;
        SignedOutPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is null) return;

        var profile = session.Profile;
        AvatarText.Text = profile.Initial;
        NameText.Text = profile.DisplayNameOrEmail;
        // 没有显示名时，上面那行读起来已经就是地址了，在下面再重复一遍纯属噪音
        EmailText.Text = profile.Email;
        EmailText.Visibility = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PlanBadgeText.Text = profile.PlanDisplay;

        PlanNameText.Text = profile.PlanDisplay;
        PlanValidityText.Text = profile.ExpiryLine
            ?? "免费版长期有效。标准版解锁单词本与四档复习。";
        NameBox.Text = profile.DisplayName;

        // 剩余数字是随用量一起到的；先在这里清空，免得用户切换账号时有一帧显示上一个账号的残留
        MonthlyLeftText.Text = "读取中…";
        DailyLeftText.Text = "读取中…";
    }

    private async Task LoadUsageAsync()
    {
        if (_account.Session is null) return;
        var result = await _account.ReadUsageAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            SetStatus(result.Error!.Display, isError: true);
            MonthlyLeftText.Text = "—";
            DailyLeftText.Text = "—";
            return;
        }

        var usage = result.Value!;
        MonthlyLeftText.Text = usage.RemainingMonthlyChars.ToString("N0") + " 字符,已用 "
            + usage.UsedMonthlyChars.ToString("N0") + " / " + usage.QuotaMonthlyChars.ToString("N0");
        MonthlyBar.Value = usage.MonthlyFraction;

        DailyRow.Visibility = usage.HasDaily ? Visibility.Visible : Visibility.Collapsed;
        if (usage.HasDaily)
        {
            DailyLeftText.Text = (usage.RemainingDailyChars ?? 0).ToString("N0") + " 字符,已用 "
                + (usage.UsedDailyChars ?? 0).ToString("N0") + " / " + usage.QuotaDailyChars!.Value.ToString("N0");
            DailyBar.Value = usage.DailyFraction;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadUsageAsync();

    private void GotoPlan_Click(object sender, RoutedEventArgs e) => PlanRequested?.Invoke();

    // ---------------- 登录 / 注册 ----------------

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;
        SwitchButton.Content = _registerMode ? "去登录" : "去注册";
        SubmitButton.Content = _registerMode ? "注册并登录" : "登录";
        CodeRow.Visibility = _registerMode && _verificationRequired ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = _registerMode
            ? "注册后自动登录,免费额度立刻可用。"
            : "登录后就能看到自己的免费额度用量。";
        SetError(null);
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (email.Length == 0) { SetError("请先填写邮箱。"); return; }

        SendCodeButton.IsEnabled = false;
        try
        {
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
                CodeHint.Text = "这个服务器不需要邮箱验证码,直接注册即可。";
                return;
            }
            CodeHint.Text = "验证码已发送,10 分钟内有效。";
            SetError(null);
        }
        finally
        {
            // 重发在服务端也有限流；本地冷却只是避免点下去没反应
            await Task.Delay(TimeSpan.FromSeconds(60));
            SendCodeButton.IsEnabled = true;
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (email.Length == 0) { SetError("请填写邮箱。"); return; }
        if (password.Length == 0) { SetError("请填写密码。"); return; }

        SetBusy(true);
        try
        {
            if (_registerMode)
            {
                var code = _verificationRequired ? CodeBox.Text.Trim() : null;
                var result = await _account.RegisterAsync(email, password, code, CancellationToken.None);
                if (!result.Succeeded) { SetError(RegisterError(result.Error!)); return; }
                PasswordBox.Clear();
                // 面板从表单切到已登录卡片是唯一反馈，用户看不出「成功」还是「什么都没发生」(v98)
                ToastWindow.Show("注册成功,已自动登录,免费额度立刻可用。");
                await LoadUsageAsync();
                return;
            }
            var signIn = await _account.SignInAsync(email, password, CancellationToken.None);
            if (!signIn.Succeeded) { SetError(signIn.Error!.Display); return; }
            PasswordBox.Clear();
            var profile = _account.Session?.Profile;
            ToastWindow.Show(profile is null
                ? "登录成功。"
                : $"登录成功:{profile.Email}({profile.PlanDisplay})");
            await LoadUsageAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string RegisterError(AccountError error) => error.Code switch
    {
        AccountErrorCode.Verification => "验证码不正确或已过期,点「获取验证码」重新发送。",
        _ => error.Display,
    };

    // ---------------- 资料编辑 ----------------

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _account.Session is null) return;
        var name = NameBox.Text.Trim();
        if (string.Equals(name, _account.Session.Profile.DisplayName, StringComparison.Ordinal))
        {
            SetStatus("名字没有变化。", isError: false);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.UpdateDisplayNameAsync(name, CancellationToken.None);
            SetStatus(result.Succeeded ? "名字已保存。" : result.Error!.Display, isError: !result.Succeeded);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var current = CurrentPasswordBox.Password;
        var next = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;

        if (current.Length == 0) { SetStatus("先填当前密码。", isError: true); return; }
        if (next.Length < 8) { SetStatus("新密码至少 8 位。", isError: true); return; }
        if (!string.Equals(next, confirm, StringComparison.Ordinal))
        {
            SetStatus("两次输入的新密码不一样。", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.ChangePasswordAsync(current, next, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetStatus(result.Error!.Display, isError: true);
                return;
            }
            CurrentPasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            SetStatus("密码已改。其他设备需要重新登录。", isError: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _account.SignOutAsync(CancellationToken.None);
        }
        finally
        {
            SetBusy(false);
        }

        _registerMode = false;
        SwitchButton.Content = "去注册";
        SubmitButton.Content = "登录";
        NameBox.Clear();
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        SetStatus(null, isError: false);
        SetError(null);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        SwitchButton.IsEnabled = !busy;
        SendCodeButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        SaveNameButton.IsEnabled = !busy;
        SavePasswordButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "请稍候…" : _registerMode ? "注册并登录" : "登录";
    }

    /// <summary>未登录错误与登录后状态在每个面板各占一个位置；只显示其中一个。</summary>
    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string? message, bool isError)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextMutedBrush");
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        SetError(null);
        SetStatus(null, isError: false);
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => SetError(null);
}
