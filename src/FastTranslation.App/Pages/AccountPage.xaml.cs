using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// 账号页：你是谁，以及围绕这件事能改的东西（名字、密码、登录设备、退出登录）。登录也在这里，
/// 因为大家找「账号」就是找这里。
///
/// <para>
/// 购买属于套餐页；本页只展示你当前拥有的内容并链过去，这样两个页面不会都自称
/// 「账号界面」。
/// </para>
/// </summary>
public partial class AccountPage : UserControl
{
    /// <summary>未登录卡片的三种形态。登录 / 注册 / 重置密码共用同一套字段，</summary>
    /// <remarks>注册与重置都靠邮箱验证码，所以不能把它们做成两张独立表单——那会让同一份
    /// 「邮箱 + 验证码 + 密码」在校验、限流和错误文案上分叉。</remarks>
    private enum AuthMode { Login, Register, Reset }

    private const string SupportEmail = AccountError.SupportEmail;

    private readonly AccountCoordinator _account;
    private AuthMode _mode = AuthMode.Login;
    private bool _busy;
    private bool _subscribed;
    private bool _verificationRequired = true;

    /// <summary>用户要求看价目表时触发；导航由外壳负责。</summary>
    public event Action? PlanRequested;

    public AccountPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        SupportEmailText.Text = SupportEmail;
        // v111：页面是缓存并反复挂载/卸载的。订阅必须跟着 Loaded/Unloaded 成对走，
        // 否则切走一次（Unloaded）就永远收不到登录/登出事件——用户登录后仍停在未登录
        // 面板、只能手动刷新，根因就在这里。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyMode();
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _account.Changed += OnAccountChanged;
            _subscribed = true;
        }
        // 订阅建立之前会话可能已经变了（启动恢复是异步的）：补一次重绘；
        // 网络数据由宿主的 Reload 拉取，这里不重复发请求。
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _account.Changed -= OnAccountChanged;
        _subscribed = false;
    }

    /// <summary>每次访问都由宿主调用：页面是缓存的，否则额度会过期不更新。</summary>
    public void Reload()
    {
        Render();
        if (_account.Session is null) return;
        _ = LoadUsageAsync();
        _ = LoadDevicesAsync();
    }

    // 会话可能在本页第一次打开之后才到（启动恢复是异步的），而那时 Reload 已经跑过了，
    // 所以迟到的账号变更还要再问一次用量
    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        Render();
        if (_account.Session is null) return;
        _ = LoadUsageAsync();
        _ = LoadDevicesAsync();
    });

    private void Render()
    {
        var session = _account.Session;
        SignedOutPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is null)
        {
            // 退出登录后清掉上一个账号的设备名，别让它们在下一帧里闪一下
            DeviceItems.ItemsSource = null;
            DeviceCountText.Text = string.Empty;
            return;
        }

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

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadUsageAsync();
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            // 这两个调用走 AccountApiClient，正常失败都会变成 AccountError；到这里是
            // 意料之外的异常，不能让它从 async void 里冒出去把进程带走。
            SetStatus("刷新失败,请稍后重试。", isError: true);
            System.Diagnostics.Debug.WriteLine("account refresh: " + ex);
        }
    }

    // ---------------- 设备管理 ----------------

    private async Task LoadDevicesAsync()
    {
        if (_account.Session is null) return;
        var result = await _account.ReadDevicesAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            DeviceItems.ItemsSource = null;
            DeviceCountText.Text = string.Empty;
            DeviceEmptyText.Text = "读不到设备列表:" + result.Error!.Display;
            DeviceEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var list = result.Value!;
        var rows = list.Devices.Select(device => new AccountDeviceRow(device)).ToList();
        DeviceItems.ItemsSource = rows;
        DeviceCountText.Text = list.CountLine;
        DeviceLimitHint.Text = $"每个账号最多 {list.MaxDevices} 台。换新电脑前，先移除不用的设备，把名额空出来。";
        DeviceEmptyText.Text = "还没有登记设备。";
        DeviceEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button button || button.Tag is not Guid deviceId) return;
        var name = button.DataContext is AccountDeviceRow row ? row.DisplayName : "这台设备";

        // 移除是破坏性操作，先问一句；系统 MessageBox 不认调色板，统一用 NoticeWindow
        if (!NoticeWindow.Confirm(Window.GetWindow(this), "移除设备",
                $"移除「{name}」后,这台设备会退出登录,需要时重新登录即可。"))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.RemoveDeviceAsync(deviceId, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetStatus(result.Error!.Display, isError: true);
                return;
            }

            ToastWindow.Show($"已移除「{name}」。");
            SetStatus("设备已移除。", isError: false);
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("移除设备时出错,请稍后重试。", isError: true);
            System.Diagnostics.Debug.WriteLine("remove device: " + ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void GotoPlan_Click(object sender, RoutedEventArgs e) => PlanRequested?.Invoke();

    // ---------------- 登录 / 注册 ----------------

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        // 重置模式的「返回登录」与注册模式的「去登录」都落到这里：只有登录能一去一回。
        _mode = _mode == AuthMode.Login ? AuthMode.Register : AuthMode.Login;
        ApplyMode();
        SetError(null);
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        _mode = AuthMode.Reset;
        ApplyMode();
        SetError(null);
    }

    /// <summary>登录/注册/重置三套文案与验证码行只在这里切换，退出登录时也必须复位。</summary>
    private void ApplyMode()
    {
        var reset = _mode == AuthMode.Reset;
        var register = _mode == AuthMode.Register;

        FormTitle.Text = reset ? "重置密码" : register ? "创建账号" : "登录账号";
        ModeHint.Text = reset
            ? "输入注册邮箱，收到验证码后设置一个新密码。"
            : register
                ? "注册后自动登录，免费额度立刻可用。"
                : "登录后免费额度、生词本与复习进度都跟着账号同步。";
        PasswordLabelText.Text = reset ? "新密码" : "密码";
        PasswordHint.Text = reset ? "至少 8 位。重置后其他设备需要重新登录。" : "至少 8 位。";
        ConfirmPasswordRow.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
        ResetHint.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
        ForgotPasswordButton.Visibility = _mode == AuthMode.Login ? Visibility.Visible : Visibility.Collapsed;
        CodeRow.Visibility = reset || (register && _verificationRequired)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SwitchPrompt.Text = reset ? "想起密码了？" : register ? "已经有账号？" : "还没有账号？";
        SwitchButton.Content = reset ? "返回登录" : register ? "去登录" : "去注册";
        SubmitButton.Content = reset ? "重置密码" : register ? "注册并登录" : "登录";
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (email.Length == 0) { SetError("请先填写邮箱。"); return; }
        if (!EmailFormat.IsValid(email)) { SetError("邮箱格式不对,检查一下有没有输错。"); return; }

        SendCodeButton.IsEnabled = false;
        try
        {
            if (_mode == AuthMode.Reset)
            {
                var reset = await _account.RequestPasswordResetCodeAsync(email, CancellationToken.None);
                if (!reset.Succeeded)
                {
                    SetError(reset.Error!.Display);
                    return;
                }
                // 服务端不区分邮箱是否存在，界面也必须保持同样的口径。
                CodeHint.Text = "如果该邮箱已注册,验证码已发送,10 分钟内有效。";
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
        // 登录也先查格式:服务端登录只查不校验,填错格式只会得到含糊的「邮箱或密码不正确」。
        if (!EmailFormat.IsValid(email)) { SetError("邮箱格式不对,检查一下有没有输错。"); return; }
        if (password.Length == 0) { SetError(_mode == AuthMode.Reset ? "请填写新密码。" : "请填写密码。"); return; }

        SetBusy(true);
        try
        {
            if (_mode == AuthMode.Reset)
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
                _mode = AuthMode.Login;
                ApplyMode();
                SetError(null);
                ToastWindow.Show("密码已重置,请用新密码登录。");
                return;
            }

            if (_mode == AuthMode.Register)
            {
                var code = _verificationRequired ? CodeBox.Text.Trim() : null;
                var result = await _account.RegisterAsync(email, password, code, CancellationToken.None);
                if (!result.Succeeded) { SetError(RegisterError(result.Error!)); return; }
                PasswordBox.Clear();
                // v111：Changed 事件负责重绘，但它是下一帧才到的；这里先切面板，
                // 「注册成功」的提示才不会比页面先出现。
                Render();
                // 面板从表单切到已登录卡片是唯一反馈，用户看不出「成功」还是「什么都没发生」(v98)
                ToastWindow.Show("注册成功,已自动登录,免费额度立刻可用。");
                await LoadUsageAsync();
                return;
            }
            var signIn = await _account.SignInAsync(email, password, CancellationToken.None);
            if (!signIn.Succeeded) { SetError(signIn.Error!.Display); return; }
            PasswordBox.Clear();
            Render();
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

        _mode = AuthMode.Login;
        ApplyMode();
        NameBox.Clear();
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        DeletePasswordBox.Clear();
        DeleteConfirmBox.Clear();
        DeletePanel.Visibility = Visibility.Collapsed;
        DeleteAccountButton.Visibility = Visibility.Visible;
        SetStatus(null, isError: false);
        SetError(null);
    }

    // ---------------- 注销与支持 ----------------

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_account.Session is null) return;
        DeletePanel.Visibility = Visibility.Visible;
        DeleteAccountButton.Visibility = Visibility.Collapsed;
        DeletePasswordBox.Clear();
        DeleteConfirmBox.Clear();
        SetStatus(null, isError: false);
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        DeletePanel.Visibility = Visibility.Collapsed;
        DeleteAccountButton.Visibility = Visibility.Visible;
        DeletePasswordBox.Clear();
        DeleteConfirmBox.Clear();
    }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _account.Session is not { } session) return;

        var password = DeletePasswordBox.Password;
        var confirmation = DeleteConfirmBox.Text.Trim();
        if (password.Length == 0) { SetStatus("先填当前密码。", isError: true); return; }
        if (!string.Equals(confirmation, session.Profile.Email, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("邮箱输入不一致。请完整输入当前账号的邮箱。", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.DeleteAccountAsync(password, confirmation, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetStatus(result.Error!.Display, isError: true);
                return;
            }

            DeletePanel.Visibility = Visibility.Collapsed;
            DeleteAccountButton.Visibility = Visibility.Visible;
            DeletePasswordBox.Clear();
            DeleteConfirmBox.Clear();
            _mode = AuthMode.Login;
            ApplyMode();
            SetError(null);
            ToastWindow.Show("账号已注销,学习记录已删除。");
        }
        catch (Exception ex)
        {
            SetStatus("注销账号时出错,请稍后重试。", isError: true);
            System.Diagnostics.Debug.WriteLine("delete account: " + ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "mailto:" + SupportEmail,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // 没装邮件客户端时 mailto 会失败；把地址显式留在界面上，用户仍能手动复制。
            SetStatus("打不开邮件客户端,请手动发到 " + SupportEmail + "。", isError: true);
            System.Diagnostics.Debug.WriteLine("support mail: " + ex);
        }
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
        ForgotPasswordButton.IsEnabled = !busy;
        DeleteAccountButton.IsEnabled = !busy;
        ConfirmDeleteButton.IsEnabled = !busy;
        CancelDeleteButton.IsEnabled = !busy;
        SupportButton.IsEnabled = !busy;
        SubmitButton.Content = busy
            ? "请稍候…"
            : _mode switch
            {
                AuthMode.Register => "注册并登录",
                AuthMode.Reset => "重置密码",
                _ => "登录",
            };
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

/// <summary>
/// 设备列表的一行。做成独立 view model 只为让绑定拿到「当前设备」对应的
/// 徽标与按钮可见性；当前设备不能被自己移除，所以不显示「移除」。
/// </summary>
public sealed class AccountDeviceRow
{
    public AccountDeviceRow(AccountDevice device)
    {
        Id = device.Id;
        DisplayName = device.DisplayName;
        DetailLine = "上次登录 " + device.LastSeenDisplay;
        CurrentBadgeVisibility = device.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        RemoveVisibility = device.IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public string DetailLine { get; }

    public Visibility CurrentBadgeVisibility { get; }

    public Visibility RemoveVisibility { get; }
}
