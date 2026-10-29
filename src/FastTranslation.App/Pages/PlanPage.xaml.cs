using System.Windows;
using System.Windows.Controls;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// 套餐页：价格、套餐包含什么、各项计量、计量如何重置，以及激活码。身份和密码在账号页——
/// 这两块以前共用一页，结果两边都受影响。
///
/// <para>
/// 这个版本没有支付通道。订阅按钮只记录用户的选择并如实说明；它从不假装会打开结算页。
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

    /// <summary>页面每次显示时由宿主调用；缓存的页面否则会变旧。</summary>
    public void Reload()
    {
        Render();
        if (_account.Session is not null) _ = LoadUsageAsync();
    }

    // 会话可能在本页第一次打开之后才到（启动恢复是异步的），所以迟到的变更
    // 必须重绘价格卡并重新询问用量
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

        // 付费套餐让推销话术变得多余：卡片变成「你已经拥有的内容」
        var paid = !string.Equals(session!.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase);
        SubscribeButton.Content = paid ? "续订" : "订阅";
        // 同一张卡片服务两类读者：订阅者不该读到「订阅以解锁」，
        // 那项功能他已经付过钱了
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

            // Render 是经 Changed 事件跑的；计量要自己再刷一次，因为回包里的用量快照
            // 只和那次兑换请求一样新。
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

    /// <summary>两张卡只选一张：移动的是边框和填充，别的一概不变。</summary>
    private void SelectPlan(bool yearly)
    {
        _yearly = yearly;
        Select(YearCard, yearly);
        Select(MonthCard, !yearly);
        // 重新选卡会清掉上一个「已记录」提示的内容
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
