using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 把 LL 钩子事件流接到协调器上：门控决策都跑在 UI 线程
/// (M0 D2)，先试 UIA，失败再回退到剪贴板。实现 Core 的取词接口。
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

    /// <summary>每次鼠标左键按下(物理像素)都会触发——用于点击外部关闭。</summary>
    public event Action<ScreenPoint>? RawMouseDown;

    /// <summary>
    /// 选区鼠标抬起时触发，并立刻携带当场捕获的选区(越新成功率越高，
    /// 见 M0)。没有可读文本时选区为空——界面仍会显示工具条。
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
        // 钩子线程 → 派发；下面的决策需要 UIA + 光标，不能耗在钩子预算里
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
        // M0 D2 门控：便宜的「光标形状」检查 OR(按下时是 I-beam + 发生了拖动)OR 语义化 UIA 检查。
        var cursorPass = s.IBeamAtUp || (s.IBeamAtDown && drag);
        var (kind, order) = Classify(s, drag);
        var processName = GetProcessName(s.SourceWindowHandle);
        // 被排除的应用连工具条都不该出现——在这里拒绝还能跳过
        // 昂贵的 UIA 门控，协调器自己的检查仍作为第二道防线
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

        // UX 方向(用户，2026-10-25)：选中文本绝不自动翻译。趁着选区还新鲜，
        // 先把选区在后台立刻捕获、缓存下来，并显示第一阶段工具条；
        // 用户显式点击「翻译」(或按热键)才带着缓存文本进入翻译流水线。
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
        _ => (TriggerKind.Hotkey, -1), // 不带拖动的普通单击：不取词
    };

    private static readonly bool DebugTrace = Environment.GetEnvironmentVariable("FT_DEBUG") == "1";

    /// <summary>供本类之外的昂贵日志字符串插值使用。</summary>
    public static bool DebugEnabled => DebugTrace;

    /// <summary>无锚点捕获(兜底失败等)时的当前光标位置。</summary>
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

    /// <summary>把源窗口(归一到它的 GA_ROOT)带到前台——
    /// 用户选完文本又切换了窗口时，剪贴板兜底会用到。</summary>
    public static void SwitchSourceToForeground(long windowHandle)
    {
        try
        {
            if (windowHandle == 0) return;
            var root = NativeMethods.GetAncestor((IntPtr)windowHandle, 2 /* GA_ROOT：顶层根窗口 */);
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
        // 失败时保留源窗口：Chromium 的 TextPattern 返回空文本的页面
        // (Qwen AI 等)仍然要能走到点击时的 Ctrl+C 兜底(v21)
        if (uia is null || string.IsNullOrEmpty(uia.Text))
            return CapturedSelection.Failed(point, sourceProcessName ?? string.Empty, fallbackWindowHandle);
        var rects = uia.Rects
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0)
            .Select(r => new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))
            .ToList();
        // 无窗口元素(Chromium)没有 hwnd——由触发源的 GA_ROOT 补上
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
        // 调用方(UI 线程)：先完整快照剪贴板(所有格式——只备份文本
        // 会毁掉图片/文件内容，bugbot P1)，然后同步发出 Ctrl+C
        var backup = ClipboardService.SnapshotClipboard();
        var beforeSeq = ClipboardService.GetSequence();
        if (!ClipboardService.SendCtrlC())
        {
            ClipboardService.RestoreClipboard(backup);
            return CapturedSelection.Failed(anchor, GetProcessName(sourceWindowHandle), sourceWindowHandle);
        }
        // 用 Win32 在工作线程上轮询(不需要 STA)——以前的 Thread.Sleep 循环
        // 跑在 UI 线程上，每次点击都把它冻住最多 1.5 秒(bugbot P1)
        var text = await Task.Run(() => ClipboardService.PollNewTextWin32(1500, beforeSeq), cancellationToken);
        ClipboardService.RestoreClipboard(backup);
        return string.IsNullOrEmpty(text)
            ? CapturedSelection.Failed(anchor, GetProcessName(sourceWindowHandle), sourceWindowHandle)
            : new CapturedSelection(
                // Ctrl+C 成功 => 源是一个编辑器；在那里用粘贴做替换/插入是可行的
                // (VSCode/Monaco 不暴露 UIA 选区，校验降级为仅前台)。
                text!, Array.Empty<ScreenRect>(), anchor, CaptureMethod.ClipboardFallback,
                GetProcessName(sourceWindowHandle), sourceWindowHandle, null, IsWritable: true);
    }

    /// <summary>最近一次鼠标抬起时捕获的选区(在此之前为空)。</summary>
    public CapturedSelection CachedSelection => Volatile.Read(ref _cached);

    /// <summary>静态辅助方法，让剪贴板兜底能复用这个读取器(写回校验)。</summary>
    public UiaSelectionReader Reader => _reader;

    public void Dispose()
    {
        _monitor.Dispose();
    }
}
