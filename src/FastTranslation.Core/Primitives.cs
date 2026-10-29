namespace FastTranslation.Core;

/// <summary>物理像素下的屏幕坐标点（鼠标钩子 / UIA 坐标）。</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>物理像素下的屏幕矩形。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(ScreenPoint p) => p.X >= X && p.X < X + Width && p.Y >= Y && p.Y < Y + Height;
}

public enum CaptureMethod
{
    UiAutomation,
    ClipboardFallback,
}

public sealed record CapturedSelection(
    string Text,
    IReadOnlyList<ScreenRect> Rects,
    ScreenPoint Anchor,
    CaptureMethod Method,
    string SourceProcessName,
    long SourceWindowHandle,
    int[]? SourceElementRuntimeId,
    bool IsWritable)
{
    public static CapturedSelection Empty(ScreenPoint anchor) => new(
        string.Empty, Array.Empty<ScreenRect>(), anchor, CaptureMethod.UiAutomation,
        string.Empty, 0, null, IsWritable: false);

    /// <summary>UIA 读不出文本时的结果（例如 Chromium 页面里 TextPattern 选区返回空）。
    /// 保留来源窗口句柄，让点击时的剪贴板兜底仍然可达——丢掉它会悄悄让那些页面上的
    /// 复制/翻译失效。</summary>
    public static CapturedSelection Failed(ScreenPoint anchor, string processName, long windowHandle) => new(
        string.Empty, Array.Empty<ScreenRect>(), anchor, CaptureMethod.UiAutomation,
        processName ?? string.Empty, windowHandle, null, IsWritable: false);
}

public enum TriggerKind
{
    DragSelect,
    DoubleClick,
    TripleClick,
    Hotkey,
    PillClick,
}

public sealed record TriggerEvent(
    TriggerKind Kind,
    ScreenPoint Point,
    long SourceWindowHandle,
    string SourceProcessName,
    long TimestampMs);
