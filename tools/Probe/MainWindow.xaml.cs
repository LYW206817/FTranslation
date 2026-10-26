using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Probe;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Read checkbox state on the UI thread only; HookProc runs on the hook thread.
        _captureOnClick = CaptureOnClickCheck.IsChecked == true;
        CaptureOnClickCheck.Checked += (_, _) => _captureOnClick = true;
        CaptureOnClickCheck.Unchecked += (_, _) => _captureOnClick = false;
        // Probe convenience: start the hook automatically so measurements can be scripted.
        // PROBE_NOHOOK=1 bisects the hook thread out of memory measurements.
        if (Environment.GetEnvironmentVariable("PROBE_NOHOOK") != "1")
        {
            Loaded += (_, _) => StartButton_Click(this, new RoutedEventArgs());
        }
        // Cache DPI while the window is rendered — GetDpi on a minimized window returns 96.
        Loaded += (_, _) => _dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
    }

    private volatile bool _captureOnClick;
    private System.Windows.DpiScale _dpi = new System.Windows.DpiScale(1.0, 1.0);
    private ScreenPoint _downPt;
    private bool _ibeamAtDown;
    private readonly int _dragThreshold = GetSystemMetrics(72); // SM_CXDRAG

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        App.FileLog(message);
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int IDC_IBEAM = 32513;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private IntPtr _hook;
    private LowLevelMouseProc? _hookProc;
    private Thread? _hookThread;
    private IntPtr _ibeam;
    private ScreenPoint _lastMouseUp;
    private Window? _probeWindow;
    private System.Windows.Threading.DispatcherTimer? _pillTimer;

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hook != IntPtr.Zero) return;
        Log($"renderTier={System.Windows.Media.RenderCapability.Tier >> 16} processRenderMode={(int)System.Windows.Media.RenderOptions.ProcessRenderMode}");
        _ibeam = LoadCursor(IntPtr.Zero, IDC_IBEAM);
        _hookThread = new Thread(() =>
        {
            _hookProc = HookProc;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Dispatcher.Invoke(() => Log($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}"));
                return;
            }
            Dispatcher.Invoke(() => Log("WH_MOUSE_LL installed."));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "Probe.MouseHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == WM_LBUTTONDOWN)
            {
                var d0 = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _downPt = new ScreenPoint(d0.pt.X, d0.pt.Y);
                _ibeamAtDown = IsIBeam();
            }
            else if (nCode >= 0 && wParam == WM_LBUTTONUP)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = new ScreenPoint(data.pt.X, data.pt.Y);
                var ib = IsIBeam();
                var dragDx = Math.Abs(pt.X - _downPt.X); var dragDy = Math.Abs(pt.Y - _downPt.Y);
                var dragged = dragDx > _dragThreshold || dragDy > _dragThreshold;
                var ibDown = _ibeamAtDown;
                var captureNow = !_captureOnClick;
                // Cursor handle equality is only a cheap pre-filter: Word always and Chromium
                // mid-drag use custom I-beam handles. The semantic decision (UIA FromPoint ->
                // text-capable element) runs on the UI thread, off the hook's timeout budget.
                _ = Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var uiaText = false;
                        if (!(ib || (ibDown && dragged))) uiaText = IsTextPoint(pt);
                        var trigger = ib || (ibDown && dragged) || uiaText;
                        App.FileLog($"dbg up at {pt.X},{pt.Y} ibeam={ib} ibDown={ibDown} drag={dragDx}x{dragDy} uiaText={uiaText} trigger={trigger}");
                        if (trigger) OnMouseUp(pt, captureNow);
                    }
                    catch (Exception ex) { Log($"OnMouseUp failed: {ex.Message}"); }
                });
            }
            else if (nCode >= 0 && wParam == WM_LBUTTONDOWN && _probeWindow is { IsVisible: true })
            {
                // Dismiss the pill on any click outside it (click on the pill keeps its capture handler).
                var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var l = _probeWindow.Left * _dpi.DpiScaleX;
                var t = _probeWindow.Top * _dpi.DpiScaleY;
                var r = l + _probeWindow.ActualWidth * _dpi.DpiScaleX;
                var b = t + _probeWindow.ActualHeight * _dpi.DpiScaleY;
                if (d.pt.X < l || d.pt.X > r || d.pt.Y < t || d.pt.Y > b)
                {
                    _ = Dispatcher.InvokeAsync(() => { try { _probeWindow.Hide(); } catch { } });
                }
            }
        }
        catch (Exception ex)
        {
            App.FileLog($"HOOKPROC failed: {ex}");
        }
        // Never throw out of a low-level hook callback: it is a fatal user callback exception.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnMouseUp(ScreenPoint pt, bool captureNow)
    {
        _lastMouseUp = pt;
        if (captureNow)
        {
            var sw = Stopwatch.StartNew();
            var selection = ReadSelectionAt(pt);
            sw.Stop();
            Log($"mouseup UIA {selection.Text.Length} chars / {selection.Rects.Count} rects / {sw.ElapsedMilliseconds}ms / {selection.Text.Replace("\n", "\\n")}");
        }
        else
        {
            Log("mouseup: pill shown, waiting for click (onClick mode)");
        }
        ShowProbeButton(pt);
    }

    private bool IsIBeam()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) && ci.hCursor == _ibeam;
    }

    private (string Text, List<ScreenRect> Rects) ReadSelectionAt(ScreenPoint pt)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            for (var i = 0; i < 10 && element is not null; i++)
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                    patternObj is TextPattern pattern)
                {
                    var ranges = pattern.GetSelection();
                    var text = string.Join("\n", ranges.Select(r => r.GetText(-1)));
                    // Plan blueprint used Chunk(4) on Rect[] and cast Rect to int — does not compile.
                    // Each bounding rectangle maps to one ScreenRect.
                    var rects = ranges
                        .SelectMany(r => r.GetBoundingRectangles())
                        .Select(r => new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height))
                        .ToList();
                    return (text, rects);
                }
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception ex)
        {
            App.FileLog($"UIA read failed at {pt.X},{pt.Y}: {ex.Message}");
        }
        return (string.Empty, []);
    }

    // Semantic text check: does the element at this point (or a near ancestor) look editable/textual?
    private bool IsTextPoint(ScreenPoint pt)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            for (var i = 0; i < 4 && element is not null; i++)
            {
                var ct = element.Current.ControlType;
                if (ct == ControlType.Document || ct == ControlType.Edit) return true;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch { }
        return false;
    }

    private void ShowProbeButton(ScreenPoint pt)
    {
        _pillTimer?.Stop();
        _pillTimer = null;
        if (_probeWindow is null)
        {
            _probeWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Width = 80,
                Height = 32,
                Content = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Child = new TextBlock
                    {
                        Text = "翻译",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
            _probeWindow.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_probeWindow).Handle;
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == WM_MOUSEACTIVATE) { handled = true; return MA_NOACTIVATE; }
                    return IntPtr.Zero;
                });
            };
            _probeWindow.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var s = ReadSelectionAt(_lastMouseUp);
                    sw.Stop();
                    Log($"click-capture UIA {s.Text.Length} chars / {s.Rects.Count} rects / {sw.ElapsedMilliseconds}ms / {s.Text.Replace("\n", "\\n")}");
                }
                catch (Exception ex) { Log($"click-capture failed: {ex.Message}"); }
            };
        }
        // Mouse hook reports physical pixels; WPF Left/Top are DIPs — convert by cached DPI scale.
        _probeWindow.Left = pt.X / _dpi.DpiScaleX;
        _probeWindow.Top = (pt.Y + 8) / _dpi.DpiScaleY;
        if (!_probeWindow.IsVisible) _probeWindow.Show();
        // Auto-hide so consecutive selections don't fight a stale pill (product behavior).
        _pillTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pillTimer.Tick += (_, _) => { _pillTimer?.Stop(); _pillTimer = null; try { _probeWindow?.Hide(); } catch { } };
        _pillTimer.Start();
    }

    private void MeasureButton_Click(object sender, RoutedEventArgs e)
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var ws = p.WorkingSet64 / 1024 / 1024;
        var priv = p.PrivateMemorySize64 / 1024 / 1024;
        MemoryText.Text = $"WS {ws}MB / Private {priv}MB / Handles {p.HandleCount}";
        Log(MemoryText.Text);
    }

    // --- write-back probe (Task 0.3) ---

    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        // Probe only: read the selection at the last mouse-up, stage the clipboard, paste over it.
        // Plan blueprint computed the point from _probeWindow.Left/Top (DIPs) — use the physical _lastMouseUp instead.
        try
        {
            var selection = ReadSelectionAt(_lastMouseUp);
            if (string.IsNullOrWhiteSpace(selection.Text)) { Log("replace: no selection to replace."); return; }
            Clipboard.SetText("REPLACED: " + selection.Text);
            var fg = GetForegroundWindow();
            uint fgPid = 0; GetWindowThreadProcessId(fg, out fgPid);
            Log($"replace: staging {selection.Text.Length} chars, fgpid={fgPid}, sending Ctrl+V");
            SendCtrlV();
            Log($"replace: pasted over {selection.Text.Length} chars.");
        }
        catch (Exception ex) { Log($"replace failed: {ex.Message}"); }
    }
}

public readonly record struct ScreenPoint(int X, int Y);
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);
