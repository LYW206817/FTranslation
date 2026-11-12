using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastTranslation.Core.Account;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 订阅订单弹窗：展示服务端下发的收款码、收集完整交易单号、轮询订单状态。
/// 付款成功由运营在后台核对后确认，客户端只负责把状态显示给用户。
/// </summary>
public partial class OrderWindow : Window
{
    private readonly AccountCoordinator _account;
    private readonly CancellationTokenSource _pollCts = new();
    private AccountOrder _order;
    private string _method = string.Empty;
    private bool _busy;
    private bool _closed;

    public OrderWindow(AccountCoordinator account, AccountOrder order)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _account = account;
        _order = order;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Render();
    }

    private void Render()
    {
        Headline.Text = _order.IsFulfilled
            ? "套餐已开通"
            : _order.Status == "cancelled" ? "订单已取消" : "订阅 " + _order.PlanDisplay;
        SummaryText.Text = $"{_order.PlanDisplay} · {_order.CycleDisplay} · {_order.Days} 天 · {_order.AmountDisplay}";
        OrderLine.Text = "订单号：" + _order.ShortId;
        AmountText.Text = _order.AmountDisplay;
        BuildMethods();

        var waiting = _order.Status == "pending_review";
        var closed = _order.IsFulfilled || _order.Status == "cancelled";
        QrImage.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        MethodRow.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        InstructionsText.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        RefLabel.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        RefBox.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        RefHint.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        SubmitButton.Visibility = closed || waiting ? Visibility.Collapsed : Visibility.Visible;
        CancelOrderButton.Visibility = waiting || closed ? Visibility.Collapsed : Visibility.Visible;
        RefBox.IsEnabled = !_busy && !waiting && !closed;
        SubmitButton.IsEnabled = !_busy && !waiting && !closed && RefBox.Text.Trim().Length > 0;

        InstructionsText.Text = "付款时在备注里写上订单号。付完后打开账单详情，把完整交易单号粘贴到下面。";
        if (_order.IsFulfilled)
        {
            ShowStatus("已确认到账，套餐已经开通。");
            CloseButton.Content = "完成";
        }
        else if (_order.Status == "cancelled")
        {
            ShowStatus(string.IsNullOrWhiteSpace(_order.CancelReason)
                ? "订单已取消。"
                : "订单已取消：" + _order.CancelReason, true);
        }
        else if (waiting)
        {
            ShowStatus("交易单号已提交，等待人工确认；确认后套餐会自动开通。");
        }
        else if (_order.PaymentMethods.Count == 0)
        {
            ShowStatus("当前没有可用的收款码，请联系 " + AccountError.SupportEmail + "。", true);
        }
        else
        {
            ShowStatus(string.Empty);
        }
    }

    private void BuildMethods()
    {
        MethodRow.Children.Clear();
        if (_order.PaymentMethods.Count == 0)
        {
            _method = string.Empty;
            QrImage.Source = null;
            return;
        }

        if (_order.PaymentMethods.All(item => item.Method != _method))
        {
            _method = _order.PaymentMethods[0].Method;
        }

        foreach (var method in _order.PaymentMethods)
        {
            var button = new Button
            {
                Content = method.Label,
                Tag = method.Method,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 12,
                Style = (Style)FindResource(method.Method == _method ? "PrimaryButton" : "SmallButton"),
            };
            button.Click += (_, _) =>
            {
                _method = method.Method;
                BuildMethods();
                ShowSelectedQr();
            };
            MethodRow.Children.Add(button);
        }

        ShowSelectedQr();
    }

    private void ShowSelectedQr()
    {
        if (_order.Status is "pending_review" or "fulfilled" or "cancelled")
        {
            QrImage.Source = null;
            return;
        }

        var selected = _order.PaymentMethods.FirstOrDefault(item => item.Method == _method);
        if (selected is null)
        {
            QrImage.Source = null;
            return;
        }

        var image = LoadQr(selected.QrDataUrl);
        QrImage.Source = image;
        if (image is null) ShowStatus("收款码加载失败，请联系 " + AccountError.SupportEmail + "。", true);
    }

    private static BitmapImage? LoadQr(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_order.Status == "pending_review") StartPolling();
    }

    private async void StartPolling()
    {
        try
        {
            await PollAsync(_pollCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭/用户取消，正常退出。
        }
        catch (Exception)
        {
            ShowStatus("查询订单状态失败，请稍后重试。", true);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!_closed && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var result = await _account.GetOrderAsync(_order.Id, cancellationToken);
            if (!result.Succeeded) continue;

            var updated = result.Value!;
            var changed = updated.Status != _order.Status;
            _order = updated;
            if (changed) Render();
            if (updated.IsFulfilled)
            {
                var profile = await _account.RefreshProfileAsync(cancellationToken);
                if (!profile.Succeeded) ShowStatus("套餐已开通，但资料刷新失败；重启应用后会更新。", true);
                return;
            }

            if (updated.Status == "cancelled") return;
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(_method))
        {
            ShowStatus("先选择微信或支付宝。", true);
            return;
        }

        var paymentRef = RefBox.Text.Trim();
        if (paymentRef.Length == 0)
        {
            ShowStatus("先把完整交易单号粘贴进来。", true);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.SubmitOrderAsync(
                _order.Id, _method, paymentRef, CancellationToken.None);
            if (!result.Succeeded)
            {
                ShowStatus(result.Error!.Display, true);
                return;
            }

            _order = result.Value!;
            Render();
            if (_order.Status == "pending_review") StartPolling();
        }
        catch (Exception)
        {
            ShowStatus("提交失败，请稍后重试。", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!NoticeWindow.Confirm(this, "取消订单", "取消后这笔订单作废，需要重新下单。"))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _account.CancelOrderAsync(_order.Id, CancellationToken.None);
            if (!result.Succeeded)
            {
                ShowStatus(result.Error!.Display, true);
                return;
            }

            ShowStatus("订单已取消。");
            Close();
        }
        catch (Exception)
        {
            ShowStatus("取消失败，请稍后重试。", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SubmitButton is null) return;
        SubmitButton.IsEnabled = !_busy && RefBox.Text.Trim().Length > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefBox.IsEnabled = !busy && _order.Status == "pending_payment";
        SubmitButton.IsEnabled = !busy && _order.Status == "pending_payment" && RefBox.Text.Trim().Length > 0;
        CancelOrderButton.IsEnabled = !busy;
        CloseButton.IsEnabled = true;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "TextSecondaryBrush");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _pollCts.Cancel();
        _pollCts.Dispose();
    }
}
