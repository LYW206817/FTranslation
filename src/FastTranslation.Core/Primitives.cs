namespace FastTranslation.Core;

/// <summary>Screen point in physical pixels (mouse-hook / UIA coordinates).</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Screen rectangle in physical pixels.</summary>
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

    /// <summary>UIA could not read the text (e.g. Chromium pages whose TextPattern selection
    /// returns empty). Keeps the source window so the click-time clipboard fallback stays
    /// reachable — losing it silently killed 复制/翻译 on those pages.</summary>
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
