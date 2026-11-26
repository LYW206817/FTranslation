using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _redeemHintIsError;
    private bool _selecting;
    private bool _subscribed;
    private bool _hasTierChoice;
    private long _selectionSession = -1;

    // 价格文案与服务端 OrderPricing.PricesCents 同批改；四个值依次是月价、季价、
    // 月标价、季标价。季付按 3 倍月价，标价按「88 万字符 10 元」线性折算。
    private static readonly PriceCopy LitePrice = new("6.9", "20.7", "10", "30");
    private static readonly PriceCopy ProPrice = new("11.9", "35.7", "21.4", "64.1");
    private static readonly PriceCopy MaxPrice = new("19.9", "59.7", "44.1", "132.3");

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
            _account.UsageRefresh.Changed += RenderUsage;
            _subscribed = true;
        }
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _account.Changed -= OnAccountChanged;
        _account.UsageRefresh.Changed -= RenderUsage;
        _subscribed = false;
    }

    /// <summary>页面每次显示时由宿主调用；缓存的页面否则会变旧。</summary>
    public void Reload()
    {
        Render();
        _ = _account.UsageRefresh.RefreshAsync();
        RenderUsage();
    }

    internal void RevealPricing()
    {
        // 主窗口延后挂载页面，等布局完成再计算价格卡在滚动视口里的位置。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsLoaded) return;
            var top = PricingCard.TranslatePoint(new Point(), PlanScroll).Y;
            _ = SubscribeTitle.Focus();
            PlanScroll.ScrollToVerticalOffset(PlanScroll.VerticalOffset + top);
        });
    }

    // 会话可能在本页第一次打开之后才到（启动恢复是异步的），所以迟到的变更
    // 必须重绘价格卡并重新询问用量
    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded) return;
        Render();
        _account.UsageRefresh.EnsureLoaded();
    });

    private void Render()
    {
        var profile = _account.Session?.Profile;
        if (_selectionSession != _account.SessionGeneration)
        {
            // 页面会被缓存。只在登录会话切换时丢弃旧选择，令牌续期和重新进页都保留用户的决定。
            _selectionSession = _account.SessionGeneration;
            _hasTierChoice = false;
            _cycle = "monthly";
        }
        var planName = profile?.PlanDisplay ?? "未登录";
        PlanPillText.Text = planName;
        PlanNameText.Text = planName;
        PlanTermText.Text = profile is null ? string.Empty : profile.Plan == "free" ? "长期有效" : "已订阅";
        SelectCycle();
        if (!_hasTierChoice) SelectTier(profile?.Plan is "light" or "max" ? profile.Plan : "standard");
        else UpdateSubscribeState();
        RenderUsage();
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _account.UsageRefresh.RefreshAsync();

    private void RenderUsage()
    {
        var refresh = _account.UsageRefresh;
        var usage = refresh.Result?.Value;
        var error = refresh.Result?.Error;
        UsageRefreshButton.Visibility = _account.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        UsageRefreshButton.IsEnabled = !refresh.IsLoading;
        UsageRefreshButton.Content = refresh.ButtonText;
        UsageStatus.Text = !_account.IsSignedIn ? "登录后才能查看用量。"
            : error?.Display ?? (refresh.IsLoading ? "正在读取用量…" : string.Empty);
        UsageStatus.Visibility = UsageStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageStatus.Foreground = (Brush)FindResource(error is null ? "TextMutedBrush" : "DangerBrush");

        UsageRing.Visibility = MonthlyUsage.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        MonthlyText.Text = usage?.MonthlyLine ?? string.Empty;
        MonthlyLeftText.Text = usage is null ? string.Empty : $"还剩 {usage.RemainingMonthlyChars:N0} 字符";
        MonthlyBar.Value = usage?.MonthlyFraction ?? 0;

        DailyRow.Visibility = usage?.HasDaily == true ? Visibility.Visible : Visibility.Collapsed;
        DailyText.Text = usage?.HasDaily == true ? usage.DailyLine : string.Empty;
        DailyLeftText.Text = usage?.HasDaily == true ? $"还剩 {usage.RemainingDailyChars ?? 0:N0} 字符" : string.Empty;
        DailyBar.Value = usage?.DailyFraction ?? 0;
    }

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var code = RedeemBox.Text.Trim();
        if (code.Length == 0)
        {
            SetRedeemHint("先把激活码填进来。", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.RedeemAsync(code, CancellationToken.None);
            if (!result.Succeeded)
            {
                SetRedeemHint(result.Error!.Display, isError: true);
                return;
            }

            // Render 是经 Changed 事件跑的；计量要自己再刷一次，因为回包里的用量快照
            // 只和那次兑换请求一样新。
            RedeemBox.Text = string.Empty;
            SetRedeemHint("已开通 " + result.Value!.Profile.PlanDisplay + "。");
            await _account.UsageRefresh.RefreshAsync();
        }
        catch (Exception)
        {
            SetRedeemHint("兑换没成功，请稍后再试。", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>兑换提示在出错时用危险色；否则与普通说明文字同色。</summary>
    private void SetRedeemHint(string text, bool isError = false)
    {
        RedeemHint.Text = text;
        _redeemHintIsError = isError;
        RedeemHint.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextMutedBrush");
    }

    // v144：卡片是 RadioButton，Checked 在鼠标、空格、方向键三种路径下都会发；
    // 程序性同步也会发，所以用 _selecting 挡住回环。
    private void Tier_Checked(object sender, RoutedEventArgs e)
    {
        if (_selecting || _busy || sender is not RadioButton { Tag: string tier }) return;
        _hasTierChoice = true;
        SelectTier(tier);
    }

    /// <summary>三张卡只选一张：选中态由模板的 IsChecked 触发器画。</summary>
    private void SelectTier(string tier)
    {
        _tier = tier;
        SyncTierCards();
        UpdateSubscribeState();
    }

    private void SyncTierCards()
    {
        _selecting = true;
        try
        {
            Select(LiteCard, _tier == "light");
            Select(ProCard, _tier == "standard");
            Select(MaxCard, _tier == "max");
        }
        finally
        {
            _selecting = false;
        }
    }

    private void Cycle_Checked(object sender, RoutedEventArgs e)
    {
        if (_selecting || _busy || sender is not RadioButton { Tag: string cycle }) return;
        _cycle = cycle;
        SelectCycle();
        UpdateSubscribeState();
    }

    private void SelectCycle()
    {
        UpdatePrices();
        _selecting = true;
        try
        {
            Select(MonthlyCycleCard, _cycle == "monthly");
            Select(QuarterlyCycleCard, _cycle == "quarterly");
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>
    /// 大价格跟着周期走：月付显示月价，季付显示 90 天总价；副行回显另一种周期与它的标价，
    /// 这样切换时用户不用自己乘 3 或除 3。
    /// </summary>
    private void UpdatePrices()
    {
        ApplyPrice(LitePriceText, LiteUnitText, LiteAltText, LitePrice);
        ApplyPrice(ProPriceText, ProUnitText, ProAltText, ProPrice);
        ApplyPrice(MaxPriceText, MaxUnitText, MaxAltText, MaxPrice);
    }

    private void ApplyPrice(TextBlock price, TextBlock unit, TextBlock alt, PriceCopy copy)
    {
        var quarterly = _cycle == "quarterly";
        price.Text = "¥" + (quarterly ? copy.Quarterly : copy.Monthly);
        unit.Text = quarterly ? "/季" : "/月";
        alt.Inlines.Clear();
        alt.Inlines.Add(new Run(quarterly
            ? $"月付 ¥{copy.Monthly} · 标价 "
            : $"季付 ¥{copy.Quarterly} · 标价 "));
        alt.Inlines.Add(new Run("¥" + (quarterly ? copy.MonthlyAnchor : copy.QuarterlyAnchor))
        {
            TextDecorations = TextDecorations.Strikethrough,
        });
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
            SubscribeHint.Text = "先到「账号」页登录，登录后才能下单。";
            return;
        }

        var current = PlanRank(session.Profile.Plan);
        var selected = PlanRank(_tier);
        if (current > 0 && selected < current)
        {
            _canSubscribe = false;
            SubscribeButton.Content = "订阅 " + TierName(_tier);
            SubscribeHint.Text = "当前套餐高于所选套餐，不能购买低档。";
        }
        else if (current > 0 && selected > current)
        {
            _canSubscribe = false;
            SubscribeButton.Content = "升级 " + TierName(_tier);
            SubscribeHint.Text = "升级套餐需要补差价，请联系 " + AccountError.SupportEmail + "。";
        }
        else
        {
            _canSubscribe = true;
            SubscribeButton.Content = (current > 0 ? "续订 " : "订阅 ") + TierName(_tier);
            SubscribeHint.Text = current > 0
                ? "续订会从当前到期日往后顺延；点按钮后显示微信 / 支付宝收款码。"
                : "点按钮后会创建订单并显示微信 / 支付宝收款码。";
        }

        SubscribeButton.IsEnabled = !_busy && _canSubscribe;
    }

    private static void Select(RadioButton card, bool selected) => card.IsChecked = selected;

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_canSubscribe) return;
        if (_account.Session is null)
        {
            SubscribeHint.Text = "先到「账号」页登录，登录后才能下单。";
            return;
        }

        // v208：查旧订单可能在网络上等待；创建请求必须用按下订阅时的选择。
        var (tier, cycle, generation) = (_tier, _cycle, _account.SessionGeneration);
        SetBusy(true);
        try
        {
            // 先恢复未结算订单：用户可能关过应用，再点订阅不应再生成一笔。
            SubscribeHint.Text = "正在检查待处理订单…";
            var current = await _account.GetCurrentOrderAsync(CancellationToken.None);
            if (_account.SessionGeneration != generation) return;
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

            SubscribeHint.Text = "正在创建订单…";
            var created = await _account.CreateOrderAsync(tier, cycle, CancellationToken.None);
            if (_account.SessionGeneration != generation) return;
            if (created.Succeeded)
            {
                ShowOrder(created.Value!);
                return;
            }

            if (created.Error!.Code == AccountErrorCode.OrderPending)
            {
                var again = await _account.GetCurrentOrderAsync(CancellationToken.None);
                if (_account.SessionGeneration != generation) return;
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
            SubscribeHint.Text = "创建订单失败，请稍后重试。";
        }
        finally
        {
            SetBusy(false);
            if (_account.SessionGeneration == generation)
            {
                SelectCycle();
                SyncTierCards();
            }
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
        TierOptions.IsEnabled = !busy;
        CycleOptions.IsEnabled = !busy;
        SubscribeButton.IsEnabled = !busy && _canSubscribe;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (_redeemHintIsError || RedeemHint.Text.StartsWith("先把激活码", StringComparison.Ordinal))
        {
            SetRedeemHint("买到激活码后填在这里，兑换后套餐立刻生效。");
        }
    }

    private readonly record struct PriceCopy(string Monthly, string Quarterly, string MonthlyAnchor, string QuarterlyAnchor);
}
