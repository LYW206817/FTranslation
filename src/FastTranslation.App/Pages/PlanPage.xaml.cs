using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastTranslation.App;
using FastTranslation.Core.Account;

namespace FastTranslation.App.Pages;

/// <summary>
/// 套餐页：价格、套餐包含什么、各项计量、计量如何重置，以及激活码。身份和密码在账号页——
/// 这两块以前共用一页，结果两边都受影响。
///
/// <para>
/// 订阅走订单：登录用户选套餐与周期后创建订单，弹窗显示微信/支付宝收款码；
/// 运营核对交易单号后直接开通。激活码路径保留给赠送与手动销售。
/// </para>
/// </summary>
public partial class PlanPage : UserControl
{
    private readonly AccountCoordinator _account;
    private bool _busy;
    private string _tier = "standard";
    private string _cycle = "monthly";
    private bool _canSubscribe = true;
    private bool _subscribed;

    public PlanPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        // v111：与账号页同一个坑——页面缓存会在导航间卸载/重新挂载，只在 Loaded 时订阅，
        // 否则从本页兑换激活码后，页面会一直停在未登录/旧套餐状态。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _account.Changed += OnAccountChanged;
            _subscribed = true;
        }
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _account.Changed -= OnAccountChanged;
        _subscribed = false;
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
            PlanPillText.Text = "未登录";
            PlanNameText.Text = "未登录";
            PlanTermText.Text = string.Empty;
            SelectCycle();
            SelectTier("standard");
            return;
        }

        var paid = !string.Equals(session!.Profile.Plan, "free", StringComparison.OrdinalIgnoreCase);
        // v91：hero 的 42px 大字与右上角药丸读的都是同一个套餐名，两处必须同源。
        var planName = session.Profile.PlanDisplay;
        PlanPillText.Text = planName;
        PlanNameText.Text = planName;
        PlanTermText.Text = paid ? "已订阅" : "长期有效";
        // 已付费时默认选中自己那一档；免费用户默认 Pro（三档里折中的一档）。
        SelectCycle();
        SelectTier(session.Profile.Plan is "light" or "standard" or "max"
            ? session.Profile.Plan
            : "standard");
    }

    /// <summary>
    /// 把本月已用比例画成那圈 118px 的用量环。
    /// WPF 没有 conic-gradient，所以环是一段圆弧：圆心取方块中心，半径 53（118/2 再让出 6px
    /// 描边的半宽），从正上方 -90° 顺时针扫过 360×比例。比例超过一半才需要大弧标志。
    /// </summary>
    private void MonthlyBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateRing(e.NewValue);

    private void UpdateRing(double fraction)
    {
        if (RingPercent is null || RingProgress is null) return;
        fraction = Math.Clamp(fraction, 0, 1);
        RingPercent.Text = (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        if (fraction < 0.002)
        {
            RingProgress.Data = null;
            return;
        }

        var figure = new PathFigure
        {
            StartPoint = RingPoint(-90),
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new ArcSegment(
            RingPoint(-90 + 360 * fraction), new Size(RingRadius, RingRadius), 0,
            fraction > 0.5, SweepDirection.Clockwise, true));
        RingProgress.Data = new PathGeometry(new[] { figure });
    }

    // v99: Ellipse 的描边画在元素盒内侧（几何 = 盒 - 描边），106 盒的环中线半径是 (106-12)/2 = 47，
    // 不是盒的一半 53。弧原先按 53 画，圆头正好骑在灰环外沿上（用户反馈）。
    private const double RingRadius = 47;

    private static Point RingPoint(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(59 + RingRadius * Math.Cos(radians), 59 + RingRadius * Math.Sin(radians));
    }

    /// <summary>读取中的占位：两条计量都写同一个词，进度条清零等真值。</summary>
    private void ShowReading()
    {
        MonthlyText.Text = "读取中…";
        MonthlyLeftText.Text = string.Empty;
        MonthlyBar.Value = 0;
        DailyText.Text = "读取中…";
        DailyLeftText.Text = string.Empty;
        DailyBar.Value = 0;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadUsageAsync();

    private async Task LoadUsageAsync()
    {
        if (_account.Session is null) return;
        UsageError.Visibility = Visibility.Collapsed;
        // v69：拿到结果前先写清楚「在读取」。原来这里什么都不写，卡片里只剩两条
        // 空进度条，看起来像坏了而不是在等网络。
        ShowReading();
        var result = await _account.ReadUsageAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            MonthlyText.Text = "读取失败";
            MonthlyLeftText.Text = string.Empty;
            DailyText.Text = string.Empty;
            DailyLeftText.Text = string.Empty;
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

    private void Tier_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tier }) SelectTier(tier);
    }

    /// <summary>三张卡只选一张：移动的是边框和填充，别的一概不变。</summary>
    private void SelectTier(string tier)
    {
        _tier = tier;
        Select(LiteCard, tier == "light");
        Select(ProCard, tier == "standard");
        Select(MaxCard, tier == "max");
        UpdateSubscribeState();
    }

    private void Cycle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cycle })
        {
            _cycle = cycle;
            SelectCycle();
            UpdateSubscribeState();
        }
    }

    private void SelectCycle()
    {
        Select(MonthlyCycleCard, _cycle == "monthly");
        Select(YearlyCycleCard, _cycle == "yearly");
    }

    private static string TierName(string tier) => tier switch
    {
        "light" => "Lite",
        "max" => "Max",
        _ => "Pro",
    };

    private static int PlanRank(string plan) => plan switch
    {
        "light" => 1,
        "standard" => 2,
        "max" => 3,
        _ => 0,
    };

    /// <summary>
    /// 订阅按钮同时表达四件事：未登录、可新开/续订、低档买高档（补差价）、高档买低档（拒绝）。
    /// 客户端先按会话里的套餐档位拦住明显不合法的选择，服务端仍会再校验一次。
    /// </summary>
    private void UpdateSubscribeState()
    {
        var session = _account.Session;
        if (session is null)
        {
            _canSubscribe = true;
            SubscribeButton.Content = "订阅 " + TierName(_tier);
            SubscribeButton.IsEnabled = !_busy;
            SubscribeHint.Text = "先到「账号」页登录,登录后才能下单。";
            return;
        }

        var current = PlanRank(session.Profile.Plan);
        var selected = PlanRank(_tier);
        if (current > 0 && selected < current)
        {
            _canSubscribe = false;
            SubscribeButton.Content = "订阅 " + TierName(_tier);
            SubscribeHint.Text = "当前套餐高于所选套餐,不能购买低档。";
        }
        else if (current > 0 && selected > current)
        {
            _canSubscribe = false;
            SubscribeButton.Content = "升级 " + TierName(_tier);
            SubscribeHint.Text = "升级套餐需要补差价,请联系 " + AccountError.SupportEmail + "。";
        }
        else
        {
            _canSubscribe = true;
            SubscribeButton.Content = (current > 0 ? "续订 " : "订阅 ") + TierName(_tier);
            SubscribeHint.Text = current > 0
                ? "续订会从当前到期日往后顺延;点按钮后显示微信 / 支付宝收款码。"
                : "点按钮后会创建订单并显示微信 / 支付宝收款码。";
        }

        SubscribeButton.IsEnabled = !_busy && _canSubscribe;
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

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_canSubscribe) return;
        if (_account.Session is null)
        {
            SubscribeHint.Text = "先到「账号」页登录,登录后才能下单。";
            return;
        }

        SetBusy(true);
        try
        {
            // 先恢复未结算订单：用户可能关过应用，再点订阅不应再生成一笔。
            var current = await _account.GetCurrentOrderAsync(CancellationToken.None);
            if (current.Succeeded)
            {
                ShowOrder(current.Value!);
                return;
            }

            if (current.Error!.Code != AccountErrorCode.OrderMissing)
            {
                SubscribeHint.Text = current.Error.Display;
                return;
            }

            var created = await _account.CreateOrderAsync(_tier, _cycle, CancellationToken.None);
            if (created.Succeeded)
            {
                ShowOrder(created.Value!);
                return;
            }

            if (created.Error!.Code == AccountErrorCode.OrderPending)
            {
                var again = await _account.GetCurrentOrderAsync(CancellationToken.None);
                if (again.Succeeded)
                {
                    ShowOrder(again.Value!);
                    return;
                }
            }

            SubscribeHint.Text = created.Error.Display;
        }
        catch (Exception)
        {
            // 下单路径只返回 AccountApiResult；这里兜住 ShowDialog 之类的意外，
            // async void 事件处理器不能让异常逃出 UI 线程。
            SubscribeHint.Text = "创建订单失败,请稍后重试。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowOrder(AccountOrder order)
    {
        var window = new OrderWindow(_account, order);
        if (Window.GetWindow(this) is { IsVisible: true } owner) window.Owner = owner;
        window.ShowDialog();
        Render();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RedeemButton.IsEnabled = !busy;
        SubscribeButton.IsEnabled = !busy && _canSubscribe;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (RedeemHint.Text.StartsWith("先把激活码", StringComparison.Ordinal))
        {
            RedeemHint.Text = "买到激活码后填在这里,兑换后套餐立刻生效。";
        }
    }
}
