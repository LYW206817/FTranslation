namespace FastTranslation.Core;

/// <summary>Platform capture surface injected into the coordinator (Core stays Win32-free).</summary>
public interface ISelectionCapture
{
    event Action<TriggerEvent>? TriggerRaised;

    /// <summary>Read the current selection near the trigger point. Returns Empty when nothing readable.</summary>
    Task<CapturedSelection> ReadSelectionAsync(ScreenPoint point, CancellationToken cancellationToken);

    /// <summary>Clipboard fallback: send Ctrl+C to source and read new clipboard text.</summary>
    Task<CapturedSelection> ReadViaClipboardAsync(long sourceWindowHandle, ScreenPoint anchor, CancellationToken cancellationToken);
}

public interface IResultPresenter
{
    void ShowLoading(string previewText, CapturedSelection source);
    void ShowCard(TranslationResult result, CapturedSelection source);
    void ShowTransientError(string message);
    void HideCard();
}

public enum WriteBackOutcome
{
    NotApplicable,
    Replaced,
    Inserted,
    CopiedInstead,
    AbortedValidation,
    AbortedForeground,
    Failed,
}

public interface IWriteBack
{
    Task<WriteBackOutcome> ReplaceAsync(CapturedSelection source, string replacement, CancellationToken cancellationToken);

    /// <summary>Collapse the selection to its end and type the translation after the original text.</summary>
    Task<WriteBackOutcome> InsertAsync(CapturedSelection source, string text, CancellationToken cancellationToken);
}
