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
    private const string SupportEmail = AccountError.SupportEmail;

    private readonly AccountCoordinator _account;
    private bool _busy;
    private bool _subscribed;
    private long? _devicesLoadingFor;
    private long _deviceLoadVersion;
    private long _renderedSession = -1;

    /// <summary>用户要求看价目表时触发；导航由外壳负责。</summary>
    public event Action? PlanRequested;

    public AccountPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        SupportEmailText.Text = SupportEmail;
        SignedOutPanel.Initialize(account);
        SignedOutPanel.Authenticated += OnAuthenticated;
        // v111：页面是缓存并反复挂载/卸载的。订阅必须跟着 Loaded/Unloaded 成对走，
        // 否则切走一次（Unloaded）就永远收不到登录/登出事件——用户登录后仍停在未登录
        // 面板、只能手动刷新，根因就在这里。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    private void OnAuthenticated()
    {
        // v111：Changed 事件里的异步调度会慢一帧；先就地切面板，让成功提示不会先于卡片切换。
        Render();
        _account.UsageRefresh.EnsureLoaded();
        _ = LoadDevicesAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _account.Changed += OnAccountChanged;
            _account.UsageRefresh.Changed += RenderUsage;
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
        _account.UsageRefresh.Changed -= RenderUsage;
        _subscribed = false;
    }

    /// <summary>每次访问都由宿主调用：页面是缓存的，否则额度会过期不更新。</summary>
    public void Reload()
    {
        Render();
        _ = _account.UsageRefresh.RefreshAsync();
        RenderUsage();
        _ = LoadDevicesAsync();
    }

    // 会话可能在本页第一次打开之后才到（启动恢复是异步的），而那时 Reload 已经跑过了，
    // 所以迟到的账号变更还要再问一次用量
    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded) return;
        var changedAccount = _renderedSession != _account.SessionGeneration;
        Render();
        _account.UsageRefresh.EnsureLoaded();
        if (changedAccount) _ = LoadDevicesAsync();
    });

    private void Render()
    {
        var session = _account.Session;
        SignedOutPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        LoginRequiredBanner.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        RenderUsage();
        if (_renderedSession != _account.SessionGeneration)
        {
            // 设备结果跟账号绑定，切换会话时先清掉上一位用户的设备名。
            _renderedSession = _account.SessionGeneration;
            DeviceItems.ItemsSource = null;
            DeviceCountText.Text = string.Empty;
            DeviceEmptyText.Text = "设备列表读取中…";
            DeviceEmptyText.Visibility = Visibility.Visible;
        }
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
        PlanValidityText.Text = profile.ExpiryLine ?? (profile.Plan == "free"
            ? "免费版长期有效。Lite / Pro / Max 解锁单词本与四档复习。"
            : "到期时间暂不可用。");
        NameBox.Text = profile.DisplayName;
    }

    private void RenderUsage()
    {
        var refresh = _account.UsageRefresh;
        var usage = refresh.Result?.Value;
        var error = refresh.Result?.Error;
        UsageRefreshButton.Content = refresh.ButtonText;
        UsageRefreshButton.IsEnabled = !refresh.IsLoading;
        UsageStatus.Text = error?.Display ?? (refresh.IsLoading ? "正在读取用量…" : string.Empty);
        UsageStatus.Visibility = UsageStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageStatus.Foreground = (System.Windows.Media.Brush)FindResource(error is null ? "TextMutedBrush" : "DangerBrush");

        MonthlyRow.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        MonthlyBar.Value = usage?.MonthlyFraction ?? 0;
        MonthlyLeftText.Text = usage is null ? string.Empty
            : $"{usage.RemainingMonthlyChars:N0} 字符，已用 {usage.UsedMonthlyChars:N0} / {usage.QuotaMonthlyChars:N0}";

        DailyRow.Visibility = usage?.HasDaily == true ? Visibility.Visible : Visibility.Collapsed;
        DailyBar.Value = usage?.DailyFraction ?? 0;
        DailyLeftText.Text = usage?.HasDaily == true
            ? $"{usage.RemainingDailyChars ?? 0:N0} 字符，已用 {usage.UsedDailyChars ?? 0:N0} / {usage.QuotaDailyChars:N0}"
            : string.Empty;
    }

    private void RefreshUsage_Click(object sender, RoutedEventArgs e) => _ = _account.UsageRefresh.RefreshAsync();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadDevicesAsync();

    private async Task LoadDevicesAsync(bool afterRemoval = false)
    {
        var generation = _account.SessionGeneration;
        if (!_account.IsSignedIn || (!afterRemoval && _devicesLoadingFor == generation)) return;
        // 移除成功后的列表必须重新读取，移除前开始的请求即使晚到也不能覆盖它。
        var version = ++_deviceLoadVersion;
        _devicesLoadingFor = generation;
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await _account.ReadDevicesAsync(CancellationToken.None);
            if (_deviceLoadVersion == version && _account.SessionGeneration == generation) RenderDevices(result);
        }
        catch (Exception ex)
        {
            // 网络错误已有结果类型；渲染或解析意外失败也要让按钮恢复，并把提示留在设备卡里。
            App.Trace($"account devices: {ex.GetType().Name}");
            if (_deviceLoadVersion == version && _account.SessionGeneration == generation)
                RenderDevices(AccountApiResult<AccountDeviceList>.Fail(AccountErrorCode.InvalidResponse));
        }
        finally
        {
            if (_deviceLoadVersion == version)
            {
                _devicesLoadingFor = null;
                RefreshButton.IsEnabled = !_busy;
            }
        }
    }

    private void RenderDevices(AccountApiResult<AccountDeviceList> result)
    {
        if (result.Value is not { } list)
        {
            DeviceItems.ItemsSource = null;
            DeviceCountText.Text = string.Empty;
            DeviceEmptyText.Text = "读不到设备列表：" + result.Error!.Display;
            DeviceEmptyText.Visibility = Visibility.Visible;
            return;
        }

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
                $"移除「{name}」后，这台设备会退出登录，需要时重新登录即可。"))
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
            await LoadDevicesAsync(afterRemoval: true);
        }
        catch (Exception ex)
        {
            SetStatus("移除设备时出错，请稍后重试。", isError: true);
            System.Diagnostics.Debug.WriteLine("remove device: " + ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void GotoPlan_Click(object sender, RoutedEventArgs e) => PlanRequested?.Invoke();

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
        catch (Exception)
        {
            SetStatus("保存失败，请稍后再试。", isError: true);
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
        catch (Exception)
        {
            SetStatus("改密码失败，请稍后再试。", isError: true);
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
        catch (Exception)
        {
            SetStatus("退出登录失败，请稍后再试。", isError: true);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        SignedOutPanel.ResetToLogin();
        NameBox.Clear();
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        DeletePasswordBox.Clear();
        DeleteConfirmBox.Clear();
        DeletePanel.Visibility = Visibility.Collapsed;
        DeleteAccountButton.Visibility = Visibility.Visible;
        SetStatus(null, isError: false);
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
            SignedOutPanel.ResetToLogin();
            ToastWindow.Show("账号已注销，学习记录已删除。");
        }
        catch (Exception ex)
        {
            SetStatus("注销账号时出错，请稍后重试。", isError: true);
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
            SetStatus("打不开邮件客户端，请手动发到 " + SupportEmail + "。", isError: true);
            System.Diagnostics.Debug.WriteLine("support mail: " + ex);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SignOutButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy && _devicesLoadingFor != _account.SessionGeneration;
        SaveNameButton.IsEnabled = !busy;
        SavePasswordButton.IsEnabled = !busy;
        DeleteAccountButton.IsEnabled = !busy;
        ConfirmDeleteButton.IsEnabled = !busy;
        CancelDeleteButton.IsEnabled = !busy;
        SupportButton.IsEnabled = !busy;
    }

    private void SetStatus(string? message, bool isError)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextMutedBrush");
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
        => SetStatus(null, isError: false);
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
