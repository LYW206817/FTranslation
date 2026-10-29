namespace FastTranslation.Core;

public enum CoordinatorState
{
    Idle,
    Capturing,
    Translating,
    Showing,
}

/// <summary>
/// 流程核心：触发 → 取词（UIA，失败走剪贴板兜底）→ 翻译 → 展示。
/// 平台相关的部分靠注入；本类从不直接碰 Win32/WPF。
/// M0 定下的规则：取词为空保持安静；点击工具条重新取词；
/// 翻译错误显示在结果卡上；写回只在用户主动操作时发生。
/// </summary>
public sealed class SelectionCoordinator : IDisposable
{
    private readonly ISelectionCapture _capture;
    private readonly IResultPresenter _presenter;
    private readonly TranslationService _translation;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _sessionCts;

    public SelectionCoordinator(
        ISelectionCapture capture,
        IResultPresenter presenter,
        TranslationService translation,
        Func<AppSettings> settings,
        Action<string>? log = null)
    {
        _capture = capture;
        _presenter = presenter;
        _translation = translation;
        _settings = settings;
        _log = log;
        _capture.TriggerRaised += OnTriggerRaised;
    }



    private void OnTriggerRaised(TriggerEvent trigger)
    {
        _ = HandleTriggerAsync(trigger);
    }

    public CoordinatorState State => CoordinatorState.Idle; // 现在改为按请求维护；为兼容保留

    /// <summary>
    /// 翻译一段已取到的选区文本（工具条入口）。新调用会取消任何在途会话——
    /// 快速连续触发绝不能被静默丢弃（用户反馈过这个问题）。
    /// </summary>
    public Task TranslateCapturedAsync(CapturedSelection selection) => RunSessionAsync(selection);

    public async Task HandleTriggerAsync(TriggerEvent trigger)
    {
        var settings0 = _settings();
        var selection = await CaptureAsync(trigger, settings0, CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrEmpty(selection.Text))
        {
            _log?.Invoke($"capture empty at {trigger.Point.X},{trigger.Point.Y} ({trigger.Kind})");
            _presenter.HideCard();
            return;
        }
        await RunSessionAsync(selection);
    }

    private async Task RunSessionAsync(CapturedSelection selection)
    {
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _sessionCts, cts);
        try { old?.Cancel(); } catch { }
        try
        {
            var settings = _settings();
            if (string.IsNullOrEmpty(selection.Text)) { _presenter.HideCard(); return; }

            _presenter.ShowLoading(selection.Text, selection);
            var result = await _translation.TranslateAsync(selection.Text, settings, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            _presenter.ShowCard(result, selection);
        }
        catch (OperationCanceledException)
        {
            // 被取代或正在关闭——不需要展示
        }
        catch (Exception ex)
        {
            _log?.Invoke($"coordinator error: {ex.Message}");
            _presenter.ShowTransientError("翻译失败,请重试");
        }
        finally
        {
            if (ReferenceEquals(_sessionCts, cts)) Interlocked.CompareExchange(ref _sessionCts, null, cts);
            cts.Dispose();
        }
    }

    private async Task<CapturedSelection> CaptureAsync(TriggerEvent trigger, AppSettings settings, CancellationToken ct)
    {
        if (IsExcluded(trigger.SourceProcessName, settings)) return CapturedSelection.Empty(trigger.Point);

        var selection = await _capture.ReadSelectionAsync(trigger.Point, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(selection.Text)) return selection;

        if (!settings.ClipboardFallbackEnabled) return selection;
        if (IsClipboardRisky(trigger.SourceProcessName)) return selection;

        var fallback = await _capture.ReadViaClipboardAsync(trigger.SourceWindowHandle, trigger.Point, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(fallback.Text) ? selection : fallback;
    }

    private static bool IsExcluded(string processName, AppSettings settings)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var excluded in settings.ExcludedProcesses)
        {
            if (string.Equals(excluded, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>在控制台宿主里 Ctrl+C 就是 SIGINT——那里绝不能模拟按键。</summary>
    public static bool IsClipboardRisky(string processName) => processName.Trim().ToLowerInvariant() is
        "windowsterminal" or "wt" or "cmd" or "powershell" or "pwsh" or "conhost" or "openconsole";

    public void Dispose()
    {
        _capture.TriggerRaised -= OnTriggerRaised;
        var cts = Interlocked.Exchange(ref _sessionCts, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }
}
