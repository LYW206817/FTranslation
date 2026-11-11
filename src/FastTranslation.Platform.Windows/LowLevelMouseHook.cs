using System.Diagnostics;
using System.Runtime.InteropServices;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

public sealed class RawMouseSnapshot
{
    public ScreenPoint DownPoint { get; init; }
    public ScreenPoint UpPoint { get; init; }
    public bool IBeamAtDown { get; init; }
    public bool IBeamAtUp { get; init; }
    public long DownTimestampMs { get; init; }
    public long UpTimestampMs { get; init; }
    public int ClickCount { get; init; }
    public long SourceWindowHandle { get; init; }
}

/// <summary>
/// T7+T8：在专用线程上挂 WH_MOUSE_LL。回调只做快照和投递；
/// 所有决策(手势分类、语义文本检查、UIA)都在接收线程上做——
/// M0 已证明：异常从钩子回调里漏出会打崩进程(c000041d)，
/// 而且 UIA 调用可能超过 LowLevelHooksTimeout。
/// </summary>
public sealed class LowLevelMouseMonitor : IDisposable
{
    private readonly object _gate = new();
    private Thread? _hookThread;
    private IntPtr _hook;
    private NativeMethods.LowLevelMouseProc? _proc;
    private readonly int _dragThreshold = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG);
    private readonly int _doubleClickPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK);
    private readonly uint _doubleClickMs = GetDoubleClickTime();
    private IntPtr _ibeam = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_IBEAM);

    // 点击串状态(仅钩子线程访问)
    private int _clickCount;
    private long _lastClickMs;
    private ScreenPoint _lastClickPoint;
    private ScreenPoint _downPoint;
    private long _downMs;
    private bool _iBeamAtDown;

    public event Action<RawMouseSnapshot>? MouseGesture;

    public event Action<ScreenPoint>? MouseButtonDown;

    private static uint GetDoubleClickTime()
    {
        [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
        return GetDoubleClickTime();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_hookThread is { IsAlive: true }) return;
            var ready = new ManualResetEventSlim(false);
            _hookThread = new Thread(() =>
            {
                _proc = HookProc;
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc,
                    NativeMethods.GetModuleHandle(null), 0);
                ready.Set();
                if (_hook == IntPtr.Zero) return;
                System.Windows.Threading.Dispatcher.Run();
            })
            { IsBackground = true, Name = "FT.MouseHook" };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            if (_hook == IntPtr.Zero)
            {
                throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = (int)wParam;
                NativeMethods.MSLLHOOKSTRUCT data;
                try { data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam); }
                catch { return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam); }

                switch (msg)
                {
                    case NativeMethods.WM_LBUTTONDOWN:
                        OnDown(new ScreenPoint(data.pt.X, data.pt.Y), data.time);
                        MouseButtonDown?.Invoke(new ScreenPoint(data.pt.X, data.pt.Y));
                        break;
                    case NativeMethods.WM_LBUTTONUP:
                        OnUp(new ScreenPoint(data.pt.X, data.pt.Y), data.time);
                        break;
                }
            }
        }
        catch
        {
            // M0：异常从低层钩子回调漏出属于致命的用户回调异常(c000041d)，会直接杀掉进程。
            // 这里吞掉；状态由观察方处理。
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }


    private void OnDown(ScreenPoint pt, uint time)
    {
        _downPoint = pt;
        _downMs = time;
        _iBeamAtDown = IsSystemIBeam();
    }

    private void OnUp(ScreenPoint pt, uint time)
    {
        var iBeamUp = IsSystemIBeam();
        var dx = Math.Abs(pt.X - _lastClickPoint.X);
        var dy = Math.Abs(pt.Y - _lastClickPoint.Y);
        _clickCount = (time - _lastClickMs <= _doubleClickMs && dx <= _doubleClickPx && dy <= _doubleClickPx)
            ? _clickCount + 1 : 1;
        _lastClickMs = time;
        _lastClickPoint = pt;

        var snapshot = new RawMouseSnapshot
        {
            DownPoint = _downPoint,
            UpPoint = pt,
            IBeamAtDown = _iBeamAtDown,
            IBeamAtUp = iBeamUp,
            DownTimestampMs = _downMs,
            UpTimestampMs = time,
            ClickCount = Math.Min(_clickCount, 3),
            SourceWindowHandle = GetSourceWindow(pt),
        };
        MouseGesture?.Invoke(snapshot);
    }

    private static long GetSourceWindow(ScreenPoint pt)
    {
        try
        {
            var hwnd = NativeExtensions.Win32WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return 0;
            var root = NativeMethods.GetAncestor(hwnd, 2 /* GA_ROOT：顶层根窗口 */);
            return (root != IntPtr.Zero ? root : hwnd).ToInt64();
        }
        catch { return 0; }
    }

    private bool IsSystemIBeam()
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        return NativeMethods.GetCursorInfo(ref ci) && ci.hCursor == _ibeam;
    }

    /// <summary>按下到抬起之间的位移超过系统拖拽阈值时返回 true。</summary>
    public bool IsDrag(RawMouseSnapshot s)
    {
        var dx = Math.Abs(s.UpPoint.X - s.DownPoint.X);
        var dy = Math.Abs(s.UpPoint.Y - s.DownPoint.Y);
        return dx > _dragThreshold || dy > _dragThreshold;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}

internal static partial class NativeExtensions
{
    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(NativeMethods.POINT point);

    internal static IntPtr Win32WindowFromPoint(ScreenPoint pt) =>
        WindowFromPoint(new NativeMethods.POINT { X = pt.X, Y = pt.Y });
}
