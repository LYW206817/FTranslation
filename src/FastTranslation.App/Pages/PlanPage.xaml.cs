using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// The 套餐 page: price, what the plan includes, the meters, how those meters reset, and the
/// activation code. Identity and password live on the 账号 page — the two used to share one page
/// and both halves suffered for it.
///
/// <para>
/// There is no payment channel in this build. The subscribe button records the choice and says so;
/// it never pretends to open a checkout.
/// </para>
/// </summary>
public partial class PlanPage : UserControl
{
    private readonly AccountCoordinator _account;
    private bool _busy;
    private bool _yearly = true;

    public PlanPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        _account.Changed += OnAccountChanged;
        Unloaded += (_, _) => _account.Changed -= OnAccountChanged;
        Render();
    }

    /// <summary>Called by the host every time the page is shown; cached pages go stale otherwise.</summary>
    public void Reload()
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    }

    // the session can arrive after this page was first opened (startup restore is async), so a late
    // change has to re-render the price card and re-ask for usage
    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    });

    private void Render()
    {
        var session = _account.Session;
        var signedIn = session is not null;

        SignedOutHint.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        DailyRow.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn)
        {
            MonthlyText.Text = "登录后显示";
            MonthlyLeftText.Text = string.Empty;
            MonthlyBar.Value = 0;
            DailyText.Text = string.Empty;
            DailyLeftText.Text = string.Empty;
            DailyBar.Value = 0;
            SubscribeTitle.Text = "订阅后解锁单词本与四档复习";
            SubscribeButton.Content = "订阅";
            return;
        }

        // a paid plan makes the pitch redundant: the card becomes "what you already have"
        var paid = !string.Equals(session!.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase);
        SubscribeButton.Content = paid ? "续订" : "订阅";
        // the same card serves both audiences: a subscriber should not read "subscribe to unlock"
        // for a feature they already paid for
        SubscribeTitle.Text = paid ? "你的套餐" : "订阅后解锁单词本与四档复习";
        if (paid)
        {
            SubscribeHint.Text = "已经是" + session.Profile.PlanDisplay
                + ",续订会顺延到期时间。付款渠道尚未接通。";
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
        MonthlyLeftText.Text = "还剩 " + usage.RemainingMonthlyChars.ToString("N0") + " 字符";
        MonthlyBar.Value = usage.MonthlyFraction;

        DailyRow.Visibility = usage.HasDaily ? Visibility.Visible : Visibility.Collapsed;
        if (usage.HasDaily)
        {
            DailyText.Text = usage.DailyLine;
            DailyLeftText.Text = "还剩 " + (usage.RemainingDailyChars ?? 0).ToString("N0") + " 字符";
            DailyBar.Value = usage.DailyFraction;
        }
    }

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var code = RedeemBox.Text.Trim();
        if (code.Length == 0)
        {
            RedeemHint.Text = "先把激活码填进来。";
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

    private void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_account.Session is null)
        {
            SubscribeHint.Text = "先到「账号」页登录,再用激活码开通。";
            return;
        }

        SubscribeHint.Text = "已记录你的选择(" + (_yearly ? "年付" : "月付")
            + ")。付款渠道上线后会通知你。";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RedeemButton.IsEnabled = !busy;
        SubscribeButton.IsEnabled = !busy;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (RedeemHint.Text.StartsWith("先把激活码", StringComparison.Ordinal))
        {
            RedeemHint.Text = "买到激活码后填在这里,兑换后套餐立刻生效。";
        }
    }
}
