using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// The account page: sign in / sign up when signed out, quota meters and the subscription card
/// when signed in. Same two panels as the old account window — a separate "usage page" would
/// duplicate the same async states — but hosted in the shell instead of its own window.
///
/// <para>
/// There is no payment channel in this build. The subscribe button records the choice and says so;
/// it never pretends to open a checkout.
/// </para>
/// </summary>
public partial class PlanPage : UserControl
{
    private readonly AccountCoordinator _account;
    private bool _registerMode;
    private bool _busy;
    private bool _yearly = true;
    private bool _verificationRequired = true;

    public PlanPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        _account.Changed += OnAccountChanged;
        Unloaded += (_, _) => _account.Changed -= OnAccountChanged;
        Render();
    }

    /// <summary>
    /// Called by the host every time the page is shown: pages are cached, so <c>Loaded</c> fires
    /// once and would leave stale quota meters after a long session.
    /// </summary>
    public void Reload()
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    }

    private void OnAccountChanged() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var session = _account.Session;
        SignedOutPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is null) return;

        AccountEmail.Text = session.Profile.Email;
        PlanText.Text = session.Profile.PlanDisplay + " · " + session.Profile.QuotaMonthly.ToString("N0") + " 字符/月"
            + (session.Profile.QuotaDaily is { } daily ? ",每日 " + daily.ToString("N0") : string.Empty)
            + (session.Profile.ExpiryLine is { } expiry ? " · " + expiry : string.Empty);

        // a paid plan makes the pitch redundant: the card becomes "what you already have"
        var paid = !string.Equals(session.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase);
        SubscribeButton.Content = paid ? "续订" : "订阅";
        // the same card serves both audiences: a subscriber should not read "subscribe to unlock"
        // for a feature they already paid for
        SubscribeTitle.Text = paid ? "你的套餐" : "订阅后解锁单词本与复习";
        if (paid && SubscribeHint.Text.StartsWith("付款渠道", StringComparison.Ordinal))
        {
            SubscribeHint.Text = "已经是" + session.Profile.PlanDisplay + ",续订会顺延到期时间。付款渠道尚未接通。";
        }
    }

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
        AccountErrorCode.EmailTaken => error.Display,
        AccountErrorCode.Verification => "验证码不正确或已过期,点「获取验证码」重新发送。",
        _ => error.Display,
    };

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var code = RedeemBox.Text.Trim();
        if (code.Length == 0)
        {
            SetError("先把激活码填进来。");
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.RedeemAsync(code, CancellationToken.None);
            if (!result.Succeeded)
            {
                RedeemHint.Text = result.Error!.Display;
                return;
            }

            // Render runs through the Changed event; the meters need their own refresh because the
            // reply's usage snapshot is only as fresh as the redemption request.
            RedeemBox.Text = string.Empty;
            RedeemHint.Text = "已开通 " + result.Value!.Profile.PlanDisplay + "。";
            await LoadUsageAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadUsageAsync();

    private async Task LoadUsageAsync()
    {
        if (_account.Session is null) return;
        UsageError.Visibility = Visibility.Collapsed;
        var result = await _account.ReadUsageAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            UsageError.Text = result.Error!.Display;
            UsageError.Visibility = Visibility.Visible;
            return;
        }
        var usage = result.Value!;
        MonthlyText.Text = usage.MonthlyLine;
        MonthlyBar.Value = usage.MonthlyFraction;
        DailyRow.Visibility = usage.HasDaily ? Visibility.Visible : Visibility.Collapsed;
        if (usage.HasDaily)
        {
            DailyText.Text = usage.DailyLine;
            DailyBar.Value = usage.DailyFraction;
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
        SetError(null);
    }

    private void Year_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectPlan(true);

    private void Month_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectPlan(false);

    /// <summary>Two cards, one selected: the border and fill move, nothing else changes.</summary>
    private void SelectPlan(bool yearly)
    {
        _yearly = yearly;
        Select(YearCard, yearly);
        Select(MonthCard, !yearly);
        // re-picking a card clears whatever the previous "recorded" message said
        SubscribeHint.Text = "付款渠道尚未接通,当前版本点击后仅记录意向。";
    }

    private void Select(Border card, bool selected)
    {
        card.BorderThickness = new Thickness(selected ? 1.5 : 1);
        card.BorderBrush = (System.Windows.Media.Brush)FindResource(
            selected ? "BrandBrush" : "HairBrush");
        card.Background = selected
            ? (System.Windows.Media.Brush)FindResource("BrandFillBrush")
            : System.Windows.Media.Brushes.Transparent;
    }

    private void Subscribe_Click(object sender, RoutedEventArgs e) =>
        SubscribeHint.Text = "已记录你的选择(" + (_yearly ? "年付" : "月付")
            + ")。付款渠道上线后会通知你。";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        SwitchButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        RedeemButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "请稍候…" : _registerMode ? "注册并登录" : "登录";
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => SetError(null);

    private void Password_Changed(object sender, RoutedEventArgs e) => SetError(null);
}
