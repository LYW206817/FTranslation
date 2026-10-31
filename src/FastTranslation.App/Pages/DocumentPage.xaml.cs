using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Documents;

namespace FastTranslation.App.Pages;

/// <summary>
/// 文档翻译页（v72，设计 §8.3）：选文件 → 预检段数与预计消耗 → 逐段翻译 → 落地 + 未译段清单。
/// <para>
/// 页面只负责「让用户看见」：真正的编排在 <see cref="DocumentTranslationCoordinator"/> 里，
/// 它把成功 / 错误 / 进度三个回调交给外面，所以这个类不碰翻译逻辑，只把回调画到控件上。
/// </para>
/// <para>
/// 页面是单例（见 <c>MainWindow.Document()</c>）：一次只跑一份文档，切走再切回来时
/// 进度与清单都还在——用户不会因为点了一下别的标签就丢掉跑到一半的作业。
/// </para>
/// </summary>
public partial class DocumentPage : UserControl
{
    private readonly DocumentTranslationCoordinator _documents;
    private readonly AccountCoordinator _account;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private string? _path;
    private DocumentPreview? _preview;
    private DocumentJobResult? _lastResult;
    private CancellationTokenSource? _cts;
    private int? _remainingChars;
    private bool _running;

    // 进度回调在后台线程上、每秒可以有几十次，攒着由计时器每 100ms 刷一次：
    // 每来一次就 Dispatcher 排队会让 200 页文档译完时 UI 线程还积着一堆过期的更新。
    private double _pendingFraction;
    private int _pendingDone;
    private int _pendingTotal;
    private bool _progressDirty;

    public DocumentPage(
        DocumentTranslationCoordinator documents,
        AccountCoordinator account,
        Func<AppSettings> settings,
        SettingsService settingsService)
    {
        InitializeComponent();

        _documents = documents;
        _account = account;
        _settings = settings;
        _settingsService = settingsService;

        LoadLanguages();
        RestoreLastFolder();
        _progressTimer.Tick += (_, _) => FlushProgress();
        Reload();
    }

    /// <summary>页面每次被切到前台时调（<c>MainWindow.RefreshPage</c>）。正在跑的时候不重读额度。</summary>
    public void Reload()
    {
        if (!_running) _ = RefreshQuotaAsync();
    }

    // ── 下面三个由 MainWindow 转发 coordinator 的回调 ─────────────────────────

    /// <summary>作业成功落地。译文文件已经在磁盘上了，这里只把结果与未译段摆出来。</summary>
    public void Present(DocumentJobResult result)
    {
        RunOnUi(() =>
        {
            _lastResult = result;
            EndRun();
            ProgressCard.Visibility = Visibility.Visible;
            ProgressText.Text = string.Format(
                CultureInfo.InvariantCulture, "已完成 {0} / {1} 段", result.TotalSegments, result.TotalSegments);
            SetProgress(1d);

            ResultCard.Visibility = Visibility.Visible;
            ResultText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} 段 · 计 {2:N0} 字符",
                Path.GetFileName(result.OutputPath),
                result.TotalSegments,
                result.Chars);

            FailedCard.Visibility = result.Failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FailedTitle.Text = string.Format(CultureInfo.InvariantCulture, "有 {0} 段没译成", result.Failed.Count);
            FailedList.ItemsSource = result.Failed;

            // 未译清单在页面最下面，而窗口高度装不下"进度 + 结果 + 清单"三张卡：
            // 不滚过去的话，有段落没译成时用户看到的和全部译成时一模一样。
            // 必须等这一轮布局跑完再滚——Visibility 刚改，此刻卡片还没有高度。
            if (result.Failed.Count > 0)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded, new Action(() => FailedCard.BringIntoView()));
            }
        });
    }

    /// <summary>失败或预检不通过的文案。重试入口留在页面上，用户不用重新选文件。</summary>
    public void ReportError(string message)
    {
        RunOnUi(() =>
        {
            EndRun();
            ErrorCard.Visibility = Visibility.Visible;
            ErrorText.Text = message;
        });
    }

    /// <summary>进度回调节流入口：只记下最新值，真正的刷新交给计时器。</summary>
    public void ReportProgress(double fraction, int done, int total)
    {
        _pendingFraction = fraction;
        _pendingDone = done;
        _pendingTotal = total;
        _progressDirty = true;
        RunOnUi(() =>
        {
            // 进度卡在这里就露出来：coordinator 第一次报进度时作业已经开工，
            // 而 RunAsync 只负责重置数值——它可能在报进度之后才轮到 UI 线程。
            ProgressCard.Visibility = Visibility.Visible;
            if (_progressTimer.IsEnabled) return;
            _progressTimer.Start();
        });
    }

    // ── 选文件 ───────────────────────────────────────────────────────────────

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要翻译的文档",
            Filter = "可翻译的文档|*.docx;*.txt;*.md|所有文件|*.*",
            CheckFileExists = true,
        };

        var folder = _settings().DocumentLastFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Select(dialog.FileName);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPath(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        var path = TryGetDroppedPath(e.Data);
        if (path is not null) Select(path);
        e.Handled = true;
    }

    private static string? TryGetDroppedPath(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return null;

        // 只认第一个文件，且必须是我们支持的扩展名：把整个文件夹拖进来时应当明确拒绝，
        // 而不是挑一个看起来像文档的文件偷偷开始翻译。
        return File.Exists(paths[0]) && DocumentFormats.TryFromPath(paths[0], out _) ? paths[0] : null;
    }

    private void Select(string path)
    {
        if (_running)
        {
            ReportError("正在翻译，请先取消或等它跑完");
            return;
        }

        _path = path;
        _preview = null;
        _lastResult = null;

        ResultCard.Visibility = Visibility.Collapsed;
        FailedCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = true;
        DropTitle.Text = Path.GetFileName(path);
        DropMeta.Text = "解析中…";

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder) && folder != _settings().DocumentLastFolder)
        {
            _settings().DocumentLastFolder = folder;
            _settingsService.Save(_settings());
        }

        _ = PreviewAsync(path);
    }

    private async Task PreviewAsync(string path)
    {
        var preview = await _documents.PreviewAsync(path, CancellationToken.None);
        if (preview is null) return; // 具体原因已经由 reportError 送到 ReportError

        if (!string.Equals(path, _path, StringComparison.Ordinal)) return; // 用户又换了一份

        _preview = preview;
        RunOnUi(() =>
        {
            DropMeta.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} 段 · 预计消耗 {2:N0} 字符",
                FormatSize(new FileInfo(path).Length),
                preview.TotalSegments,
                preview.Chars);
            UpdateQuotaText();
        });

        await RefreshQuotaAsync();
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? string.Format(CultureInfo.InvariantCulture, "{0:N1} MB", bytes / (1024d * 1024d))
        : string.Format(CultureInfo.InvariantCulture, "{0:N0} KB", Math.Max(1, bytes / 1024d));

    // ── 额度与语言 ──────────────────────────────────────────────────────────

    private async Task RefreshQuotaAsync()
    {
        if (!_account.IsSignedIn)
        {
            _remainingChars = null;
            RunOnUi(UpdateQuotaText);
            return;
        }

        var usage = await _account.ReadUsageAsync(CancellationToken.None);
        _remainingChars = usage.Succeeded ? usage.Value!.RemainingMonthlyChars : null;
        RunOnUi(UpdateQuotaText);
    }

    private void UpdateQuotaText()
    {
        if (!_account.IsSignedIn)
        {
            QuotaText.Text = "未登录：会用你自己的 Key 翻译，不占免费额度，也不做额度预检。";
            return;
        }

        if (_remainingChars is not { } remaining)
        {
            QuotaText.Text = "月度额度读取失败，这次不做预检。";
            return;
        }

        var line = string.Format(CultureInfo.InvariantCulture, "本月剩余 {0:N0} 字符", remaining);
        if (_preview is { Chars: > 0 } preview)
        {
            var percent = Math.Round(preview.Chars * 100d / remaining, 0);
            line += string.Format(
                CultureInfo.InvariantCulture,
                "，这份约 {0:N0} 字符（占 {1:N0}%）",
                preview.Chars,
                percent);
        }

        QuotaText.Text = line;
    }

    private void LoadLanguages()
    {
        foreach (var language in LanguageCatalog.Languages)
        {
            TargetCombo.Items.Add(new ComboBoxItem { Content = language.DisplayName, Tag = language.Code });
        }

        SelectTag(_settings().DocumentTargetLanguage);
    }

    private void SelectTag(string tag)
    {
        for (var i = 0; i < TargetCombo.Items.Count; i++)
        {
            if (TargetCombo.Items[i] is ComboBoxItem item && (string)item.Tag == tag)
            {
                TargetCombo.SelectedIndex = i;
                return;
            }
        }

        SelectTag(LanguageCatalog.EnglishCode);
    }

    private string TargetLanguage =>
        TargetCombo.SelectedItem is ComboBoxItem { Tag: string code } ? code : LanguageCatalog.EnglishCode;

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _settings().DocumentTargetLanguage = TargetLanguage;
        _settingsService.Save(_settings());
    }

    private void RestoreLastFolder()
    {
        var language = LanguageCatalog.TryGet(_settings().DocumentTargetLanguage, out var info)
            ? info.DisplayName
            : _settings().DocumentTargetLanguage;
        DropMeta.Text = "目标语言：" + language;
    }

    // ── 翻译 ────────────────────────────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private async Task RunAsync()
    {
        if (_path is not { } path || _running) return;

        _running = true;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ProgressCard.Visibility = Visibility.Visible;
        ErrorCard.Visibility = Visibility.Collapsed;
        _pendingDone = 0;
        _pendingTotal = _preview?.TotalSegments ?? 0;
        SetProgress(0);

        try
        {
            await _documents.RunAsync(path, TargetLanguage, _remainingChars, _cts.Token);
        }
        finally
        {
            EndRun();
            await RefreshQuotaAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 取消后断点留着，下次对同一份文件再点开始会从已译完的地方接着译。
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void EndRun()
    {
        _running = false;
        _progressTimer.Stop();
        _progressDirty = false;
        CancelButton.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = _path is not null;
        RetryButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
    }

    // ── 完成态 ──────────────────────────────────────────────────────────────

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is not { } result) return;

        try
        {
            // explorer /select 会打开文件夹并选中译文，比只 ShellExecute 目录更省一步手动查找。
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + result.OutputPath + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private void Again_Click(object sender, RoutedEventArgs e)
    {
        _path = null;
        _preview = null;
        _lastResult = null;
        DropTitle.Text = "把 docx / txt / md 拖到这里";
        StartButton.IsEnabled = false;
        ResultCard.Visibility = Visibility.Collapsed;
        FailedCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Collapsed;
        RestoreLastFolder();
    }

    // ── 进度刷新 ────────────────────────────────────────────────────────────

    private void FlushProgress()
    {
        if (!_progressDirty)
        {
            _progressTimer.Stop();
            return;
        }

        _progressDirty = false;
        SetProgress(_pendingFraction);
        ProgressText.Text = _pendingTotal == 0
            ? "解析中…"
            : string.Format(CultureInfo.InvariantCulture, "已完成 {0} / {1} 段", _pendingDone, _pendingTotal);
    }

    private void SetProgress(double fraction)
    {
        Progress.Value = Math.Clamp(fraction, 0d, 1d);
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }
}
