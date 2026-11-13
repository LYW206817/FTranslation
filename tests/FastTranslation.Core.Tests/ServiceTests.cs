using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public sealed class FakeProvider : ITranslationProvider
{
    public volatile int Calls;
    public Func<TranslationRequest, TranslationResult> Handler = r =>
        new TranslationResult(r.Text, "译:" + r.Text, r.SourceLanguage, r.TargetLanguage, r.Model, 10, false);

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Handler(request));
    }

    public void Dispose() { }
}

public class TranslationServiceTests
{
    private static AppSettings Settings() => new() { Model = "m", TargetLanguage = "auto" };

    [Fact]
    public async Task EmptyInput_ShortCircuits_WithoutProviderCall()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider);
        var result = await service.TranslateAsync("   ", Settings());
        Assert.False(result.Succeeded);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task TooLongInput_FailsFast()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider);
        var result = await service.TranslateAsync(new string('a', TranslationRequest.MaxInputLength + 1), Settings());
        Assert.Equal(TranslationErrorCode.ContentTooLong, result.Error?.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task SecondIdenticalRequest_HitsCache()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider);
        await service.TranslateAsync("hello", Settings());
        var second = await service.TranslateAsync("hello", Settings());
        Assert.True(second.FromCache);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task NewRequest_Cancels_PreviousInFlight()
    {
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider
        {
            Handler = r =>
            {
                if (r.Text == "slow") { entered.TrySetResult(); gate.Task.Wait(5000); }
                return new TranslationResult(r.Text, "x", r.SourceLanguage, r.TargetLanguage, r.Model, 1, false);
            },
        };
        var service = new TranslationService(provider);

        var slowTask = Task.Run(() => service.TranslateAsync("slow", Settings()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); // 此时慢请求正停在被伪造的 provider 内部
        var fast = await service.TranslateAsync("fast", Settings());
        Assert.True(fast.Succeeded);

        gate.TrySetResult();
        var slowResult = await slowTask;
        Assert.Equal(TranslationErrorCode.Cancelled, slowResult.Error?.Code);
    }
}

public class SelectionCoordinatorTests
{
    private sealed class FakeCapture : ISelectionCapture
    {
        public event Action<TriggerEvent>? TriggerRaised;
        public Func<ScreenPoint, CapturedSelection> OnRead = _ => CapturedSelection.Empty(default);
        public int FallbackCalls;
        public bool FallbackReturnsText = true;

        public Task<CapturedSelection> ReadSelectionAsync(ScreenPoint point, CancellationToken ct) => Task.FromResult(OnRead(point));

        public Task<CapturedSelection> ReadViaClipboardAsync(long hwnd, ScreenPoint anchor, CancellationToken ct)
        {
            FallbackCalls++;
            return Task.FromResult(FallbackReturnsText
                ? new CapturedSelection("clip text", Array.Empty<ScreenRect>(),
                    default, CaptureMethod.ClipboardFallback, "notepad", hwnd, null, false)
                : CapturedSelection.Failed(anchor, "notepad", hwnd));
        }

        public void Raise(TriggerEvent e) => TriggerRaised?.Invoke(e);
    }

    private sealed class FakePresenter : IResultPresenter
    {
        public List<string> Shown = new();
        public void ShowLoading(string previewText, CapturedSelection source) => Shown.Add("loading");
        public void ShowCard(TranslationResult result, CapturedSelection source) => Shown.Add("card:" + result.TranslatedText);
        public void ShowTransientError(string message) => Shown.Add("err:" + message);
        public void HideCard() => Shown.Add("hide");
    }

    [Fact]
    public async Task EmptyCapture_Notifies_WhenFallbackDisabled()
    {
        var capture = new FakeCapture();
        var presenter = new FakePresenter();
        var service = new TranslationService(new FakeProvider());
        var settings = AppSettings.Default();
        settings.ClipboardFallbackEnabled = false;
        CaptureEmptyReason? reason = null;
        using var coordinator = new SelectionCoordinator(
            capture, presenter, service, () => settings, captureEmptyHint: r => reason = r);

        await coordinator.HandleTriggerAsync(new TriggerEvent(TriggerKind.DragSelect, new ScreenPoint(1, 1), 5, "notepad", 0));

        Assert.Equal(CaptureEmptyReason.ClipboardDisabled, reason);
        Assert.DoesNotContain(presenter.Shown, s => s.StartsWith("card"));
        Assert.Equal(0, capture.FallbackCalls);
    }

    [Fact]
    public async Task EmptyCapture_Notifies_WhenClipboardHasNoText()
    {
        var capture = new FakeCapture { FallbackReturnsText = false };
        var presenter = new FakePresenter();
        var service = new TranslationService(new FakeProvider());
        CaptureEmptyReason? reason = null;
        using var coordinator = new SelectionCoordinator(
            capture, presenter, service, () => AppSettings.Default(), captureEmptyHint: r => reason = r);

        await coordinator.HandleTriggerAsync(new TriggerEvent(TriggerKind.DragSelect, new ScreenPoint(1, 1), 5, "notepad", 0));

        Assert.Equal(CaptureEmptyReason.ClipboardNoText, reason);
        Assert.Equal(1, capture.FallbackCalls);
        Assert.DoesNotContain(presenter.Shown, s => s.StartsWith("card"));
    }

    [Fact]
    public async Task EmptyCapture_UsesClipboardFallback_WhenEnabled()
    {
        var capture = new FakeCapture();
        var presenter = new FakePresenter();
        var service = new TranslationService(new FakeProvider());
        using var coordinator = new SelectionCoordinator(capture, presenter, service, () => AppSettings.Default());

        await coordinator.HandleTriggerAsync(new TriggerEvent(TriggerKind.DragSelect, new ScreenPoint(1, 1), 5, "notepad", 0));

        Assert.Equal(1, capture.FallbackCalls);
        Assert.Contains(presenter.Shown, s => s.StartsWith("card:译:clip text"));
    }

    [Fact]
    public async Task TerminalHost_NeverUsesClipboardFallback()
    {
        var capture = new FakeCapture();
        var presenter = new FakePresenter();
        var service = new TranslationService(new FakeProvider());
        CaptureEmptyReason? reason = null;
        using var coordinator = new SelectionCoordinator(
            capture, presenter, service, () => AppSettings.Default(), captureEmptyHint: r => reason = r);

        await coordinator.HandleTriggerAsync(new TriggerEvent(TriggerKind.DragSelect, new ScreenPoint(1, 1), 5, "WindowsTerminal", 0));

        Assert.Equal(0, capture.FallbackCalls);
        Assert.Equal(CaptureEmptyReason.ClipboardRisky, reason);
    }

    [Fact]
    public async Task ExcludedProcess_Ignored()
    {
        var capture = new FakeCapture();
        var presenter = new FakePresenter();
        var service = new TranslationService(new FakeProvider());
        var settings = AppSettings.Default();
        settings.ExcludedProcesses.Add("PrivateApp");
        var hints = 0;
        using var coordinator = new SelectionCoordinator(
            capture, presenter, service, () => settings, captureEmptyHint: _ => hints++);

        await coordinator.HandleTriggerAsync(new TriggerEvent(TriggerKind.DragSelect, new ScreenPoint(1, 1), 5, "PrivateApp", 0));

        Assert.Equal(0, hints);
        Assert.DoesNotContain(presenter.Shown, s => s.StartsWith("card"));
    }
}
