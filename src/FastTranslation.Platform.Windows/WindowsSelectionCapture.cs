using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// Glues the LL-hook stream to the coordinator: gate decisions run on the UI thread
/// (M0 D2), UIA first, clipboard fallback second. Implements Core's capture surface.
/// </summary>
public sealed class WindowsSelectionCapture : ISelectionCapture, IDisposable
{
    private readonly LowLevelMouseMonitor _monitor;
    private readonly UiaSelectionReader _reader;
    private readonly ClipboardService _clipboard;

    private readonly Func<bool> _fallbackEnabled;
    private readonly Func<string, bool> _isExcluded;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string>? _log;
    private CapturedSelection _cached = CapturedSelection.Empty(default);

    public event Action<TriggerEvent>? TriggerRaised;

    /// <summary>Raised for every left-button-down (physical px) — used for outside-click dismissal.</summary>
    public event Action<ScreenPoint>? RawMouseDown;

    /// <summary>
    /// Raised on selection mouse-up with the selection captured right away (fresh = highest
    /// success rate; M0). Selection is empty when nothing readable — UI still shows the toolbar.
    /// </summary>
    public event Action<CapturedSelection>? SelectionToolbarRequested;

    public WindowsSelectionCapture(
        Dispatcher dispatcher,
        Func<bool>? fallbackEnabled = null,
        Action<string>? log = null,
        Func<string, bool>? isExcluded = null)
    {
        _dispatcher = dispatcher;
        _fallbackEnabled = fallbackEnabled ?? (() => true);
        _isExcluded = isExcluded ?? (_ => false);
        _log = log;
        _monitor = new LowLevelMouseMonitor();
        _reader = new UiaSelectionReader();
        _clipboard = new ClipboardService();
        _monitor.MouseGesture += OnGesture;
        _monitor.MouseButtonDown += pt => _dispatcher.BeginInvoke(() =>
        {
            try { RawMouseDown?.Invoke(pt); } catch { }
        });
    }

    public void Start() => _monitor.Start();

    private void OnGesture(RawMouseSnapshot snapshot)
    {
        // hook thread → marshal; decisions below need UIA + cursor, off the hook budget
        _dispatcher.BeginInvoke(() =>
        {
            try { HandleGesture(snapshot); }
            catch (Exception ex) { _log?.Invoke($"gate error: {ex.Message}"); }
        });
    }

    private void HandleGesture(RawMouseSnapshot s)
    {
        Diag($"gesture up={s.UpPoint.X},{s.UpPoint.Y} clicks={s.ClickCount} ibUp={s.IBeamAtUp} ibDown={s.IBeamAtDown}");
        var drag = _monitor.IsDrag(s);
        // M0 D2 gate: cheap cursor pass OR (I-beam at down + drag) OR semantic UIA check.
        var cursorPass = s.IBeamAtUp || (s.IBeamAtDown && drag);
        var (kind, order) = Classify(s, drag);
        var processName = GetProcessName(s.SourceWindowHandle);
        // excluded apps must not even reach the toolbar — rejecting here also skips the
        // expensive UIA gate, and the coordinator's own check stays as the second line
        if (_isExcluded(processName))
        {
            Diag($"gate reject kind={kind} proc={processName} reason=excluded");
            return;
        }
        if (!cursorPass && !UiaSelectionReader.LooksTextual(s.UpPoint))
        {
            Diag($"gate reject kind={kind} proc={processName} hwnd=0x{s.SourceWindowHandle:X}");
            return;
        }
        Diag($"gate pass kind={kind} proc={processName} hwnd=0x{s.SourceWindowHandle:X}");
        var trigger = new TriggerEvent(
            kind, s.UpPoint, s.SourceWindowHandle, processName, Environment.TickCount64);

        // UX direction (user, 2026-10-25): selection NEVER auto-translates. Capture immediately
        // in the background while the selection is fresh, cache it, show the stage-1 toolbar;
        // the explicit 翻译 click (or hotkey) enters the translate pipeline with the cached text.
        if (kind != TriggerKind.Hotkey && kind != TriggerKind.PillClick)
        {
            var point = s.UpPoint;
            var hwnd = s.SourceWindowHandle;
            var sourceProcess = GetProcessName(hwnd);
            _ = Task.Run(() =>
            {
                CapturedSelection captured;
                try { captured = ReadSelectionAt(point, sourceProcess, hwnd); }
                catch { captured = CapturedSelection.Failed(point, sourceProcess ?? string.Empty, hwnd); }
                _dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _cached = captured;
                        Diag("toolbar requested, captured=" + captured.Text.Length + "ch writable=" + captured.IsWritable);
                        SelectionToolbarRequested?.Invoke(captured);
                    }
                    catch (Exception ex) { _log?.Invoke($"toolbar event error: {ex.Message}"); }
                });
            });
            return;
        }
        if (order >= 0) TriggerRaised?.Invoke(trigger);
    }

    private static (TriggerKind kind, int order) Classify(RawMouseSnapshot s, bool drag) => s.ClickCount switch
    {
        2 => (TriggerKind.DoubleClick, 0),
        >= 3 => (TriggerKind.TripleClick, 0),
        _ when drag => (TriggerKind.DragSelect, 0),
        _ => (TriggerKind.Hotkey, -1), // plain single click without drag: no capture
    };

    private static readonly bool DebugTrace = Environment.GetEnvironmentVariable("FT_DEBUG") == "1";

    /// <summary>For expensive log-string interpolation outside this class.</summary>
    public static bool DebugEnabled => DebugTrace;

    /// <summary>Current cursor position for anchor-less captures (fallback errors etc.).</summary>
    public static ScreenPoint CurrentCursorPoint()
    {
        try
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (NativeMethods.GetCursorInfo(ref ci)) return new ScreenPoint(ci.ptScreenPos.X, ci.ptScreenPos.Y);
        }
        catch { }
        return default;
    }

    /// <summary>Bring a source window (normalized to its GA_ROOT) to the foreground —
    /// used by the clipboard fallback when the user switched windows after selecting.</summary>
    public static void SwitchSourceToForeground(long windowHandle)
    {
        try
        {
            if (windowHandle == 0) return;
            var root = NativeMethods.GetAncestor((IntPtr)windowHandle, 2 /* GA_ROOT */);
            var target = root != IntPtr.Zero ? root : (IntPtr)windowHandle;
            if (NativeMethods.GetForegroundWindow() == target) return;
            NativeMethods.SwitchToThisWindow(target, false);
        }
        catch { }
    }

    public static void Diag(string message)
    {
        if (!DebugTrace) return;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ft-debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine); } catch { }
    }

    private static string GetProcessName(long hwnd)
    {
        try
        {
            if (hwnd == 0) return string.Empty;
            NativeMethods.GetWindowThreadProcessId((IntPtr)hwnd, out var pid);
            if (pid == 0) return string.Empty;
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return string.Empty; }
    }

    private CapturedSelection ReadSelectionAt(ScreenPoint point, string? sourceProcessName = null, long fallbackWindowHandle = 0)
    {
        var uia = _reader.ReadAt(point, sourceProcessName);
        // On failure keep the source window: pages whose Chromium TextPattern returns empty
        // (Qwen AI etc.) must still reach the click-time Ctrl+C fallback (v21)
        if (uia is null || string.IsNullOrEmpty(uia.Text))
            return CapturedSelection.Failed(point, sourceProcessName ?? string.Empty, fallbackWindowHandle);
        var rects = uia.Rects
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0)
            .Select(r => new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))
            .ToList();
        // windowless elements (Chromium) carry no hwnd — the trigger's GA_ROOT fills in
        var sourceWindow = uia.SourceWindowHandle != 0 ? uia.SourceWindowHandle : fallbackWindowHandle;
        return new CapturedSelection(
            uia.Text, rects, point, CaptureMethod.UiAutomation,
            !string.IsNullOrEmpty(sourceProcessName) ? sourceProcessName : GetProcessName(sourceWindow),
            sourceWindow, uia.RuntimeId, uia.IsWritable);
    }

    public Task<CapturedSelection> ReadSelectionAsync(ScreenPoint point, CancellationToken cancellationToken)
    {
        var uia = _reader.ReadAt(point);
        if (uia is null || string.IsNullOrEmpty(uia.Text))
        {
            return Task.FromResult(CapturedSelection.Empty(point));
        }
        var rects = uia.Rects
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0)
            .Select(r => new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))
            .ToList();
        return Task.FromResult(new CapturedSelection(
            uia.Text, rects, point, CaptureMethod.UiAutomation,
            GetProcessName(uia.SourceWindowHandle), uia.SourceWindowHandle,
            uia.RuntimeId, uia.IsWritable));
    }

    public async Task<CapturedSelection> ReadViaClipboardAsync(
        long sourceWindowHandle, ScreenPoint anchor, CancellationToken cancellationToken)
    {
        if (!_fallbackEnabled())
            return CapturedSelection.Failed(anchor, GetProcessName(sourceWindowHandle), sourceWindowHandle);
        // caller (UI thread): snapshot the FULL clipboard (all formats — text-only backup
        // destroyed image/file content, bugbot P1), then send Ctrl+C synchronously
        var backup = ClipboardService.SnapshotClipboard();
        var beforeSeq = ClipboardService.GetSequence();
        if (!ClipboardService.SendCtrlC())
        {
            ClipboardService.RestoreClipboard(backup);
            return CapturedSelection.Failed(anchor, GetProcessName(sourceWindowHandle), sourceWindowHandle);
        }
        // poll on a worker thread via Win32 (no STA needed) — the old Thread.Sleep loop
        // ran on the UI thread and froze it for up to 1.5 s per click (bugbot P1)
        var text = await Task.Run(() => ClipboardService.PollNewTextWin32(1500, beforeSeq), cancellationToken);
        ClipboardService.RestoreClipboard(backup);
        return string.IsNullOrEmpty(text)
            ? CapturedSelection.Failed(anchor, GetProcessName(sourceWindowHandle), sourceWindowHandle)
            : new CapturedSelection(
                // Ctrl+C worked => the source is an editor; paste-based replace/insert is valid
                // there (VSCode/Monaco exposes no UIA selection, validation degrades to fg-only).
                text!, Array.Empty<ScreenRect>(), anchor, CaptureMethod.ClipboardFallback,
                GetProcessName(sourceWindowHandle), sourceWindowHandle, null, IsWritable: true);
    }

    /// <summary>The selection captured at the last mouse-up (empty until then).</summary>
    public CapturedSelection CachedSelection => Volatile.Read(ref _cached);

    /// <summary>Static helper so the clipboard fallback can reuse the reader (write-back validation).</summary>
    public UiaSelectionReader Reader => _reader;

    public void Dispose()
    {
        _monitor.Dispose();
    }
}
