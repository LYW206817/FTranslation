namespace FastTranslation.Core;

/// <summary>注入到协调器里的平台取词接口（Core 保持不依赖 Win32）。</summary>
public interface ISelectionCapture
{
    event Action<TriggerEvent>? TriggerRaised;

    /// <summary>读取触发点附近的当前选区。读不到内容时返回 Empty。</summary>
    Task<CapturedSelection> ReadSelectionAsync(ScreenPoint point, CancellationToken cancellationToken);

    /// <summary>剪贴板兜底：向来源窗口发 Ctrl+C，再读新的剪贴板文本。</summary>
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

    /// <summary>把选区折叠到末尾，然后在原文之后输入译文。</summary>
    Task<WriteBackOutcome> InsertAsync(CapturedSelection source, string text, CancellationToken cancellationToken);
}
