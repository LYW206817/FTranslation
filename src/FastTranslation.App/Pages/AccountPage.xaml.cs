using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// The account page: who you are and the three things you can change about that (name, password,
/// sign out). Signing in also lives here, because "账号" is where people look for it.
///
/// <para>
/// Buying belongs to the 套餐 page; this page only shows what you currently have and links there,
/// so the two pages never both claim to be "the account screen".
/// </para>
/// </summary>
public partial class AccountPage : UserControl
{
    private readonly AccountCoordinator _account;
    private bool _registerMode;
    private bool _busy;
    private bool _verificationRequired = true;

    /// <summary>Raised when the user asks for the price list; the shell owns navigation.</summary>
    public event Action? PlanRequested;

    public AccountPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        _account.Changed += OnAccountChanged;
        Unloaded += (_, _) => _account.Changed -= OnAccountChanged;
        Render();
    }

    /// <summary>Called by the host on every visit: pages are cached, so quotas would go stale.</summary>
    public void Reload()
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    }

    // the session can arrive after this page was first opened (startup restore is async), and
    // Reload already ran by then — so a late account change re-asks for usage as well
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
        // without a display name the line above already reads as the address, so repeating it
        // underneath is just noise
        EmailText.Text = profile.Email;
        EmailText.Visibility = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PlanBadgeText.Text = profile.PlanDisplay;

        PlanNameText.Text = profile.PlanDisplay;
        PlanValidityText.Text = profile.ExpiryLine
            ?? "免费版长期有效。标准版解锁单词本与四档复习。";
        NameBox.Text = profile.DisplayName;

        // the remaining numbers arrive with usage; clearing them here avoids showing the previous
        // account's leftovers for a frame when the user switches accounts
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

    // ---------------- sign in / sign up ----------------

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
                // this server does not verify email: stop asking for a code that will never arrive
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
            // a resend is rate limited server-side too; the local cooldown just avoids a dead click
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
                await LoadUsageAsync();
                return;
            }
            var signIn = await _account.SignInAsync(email, password, CancellationToken.None);
            if (!signIn.Succeeded) { SetError(signIn.Error!.Display); return; }
            PasswordBox.Clear();
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

    // ---------------- profile edits ----------------

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

    /// <summary>Signed-out errors and signed-in status share one slot per panel; only one shows.</summary>
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
