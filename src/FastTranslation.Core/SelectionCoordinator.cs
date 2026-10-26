namespace FastTranslation.Core;

public enum CoordinatorState
{
    Idle,
    Capturing,
    Translating,
    Showing,
}

/// <summary>
/// Heart of the flow: trigger → capture (UIA, clipboard fallback) → translate → present.
/// Platform bits are injected; this class never touches Win32/WPF.
/// M0-derived rules: empty capture stays silent; pill click retries capture;
/// translation errors surface on the card; write-back only on user action.
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

    public CoordinatorState State => CoordinatorState.Idle; // per-request now; kept for compat

    /// <summary>
    /// Translate an already-captured selection (toolbar path). A new call CANCELS any
    /// in-flight session — rapid re-triggers must never be silently dropped (user feedback).
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
            // superseded or shutting down — nothing to show
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

    /// <summary>Ctrl+C in a console host is SIGINT — never simulate it there.</summary>
    public static bool IsClipboardRisky(string processName) => processName.Trim().ToLowerInvariant() is
        "windowsterminal" or "wt" or "cmd" or "powershell" or "pwsh" or "conhost" or "openconsole";

    public void Dispose()
    {
        _capture.TriggerRaised -= OnTriggerRaised;
        var cts = Interlocked.Exchange(ref _sessionCts, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }
}
