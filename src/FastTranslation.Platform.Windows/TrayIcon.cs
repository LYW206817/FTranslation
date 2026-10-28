using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T12: native Shell_NotifyIcon tray icon (no WinForms). Single-click toggles capture,
/// context menu: 设置/暂停取词/退出. No logging, no telemetry.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_TRAYICON = 0x8000 + 1;
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1;
    private const uint NIF_ICON = 2;
    private const uint NIF_TIP = 4;
    private const uint NIF_INFO = 0x10;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativeMethods.POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly HwndSource _sink;
    private IntPtr _icon;
    private NOTIFYICONDATA _data;
    private bool _added;

    public event Action? SettingsRequested;
    public event Action<bool>? CaptureToggled; // true = enabled
    public event Action? ExitRequested;
    public event Action? MainWindowRequested;
    public event Action? UpdateRequested;
    public event Action? AccountRequested;
    public event Action? NotesRequested;
    public event Action? ScreenshotTranslateRequested;

    private bool _captureEnabled = true;

    private readonly byte[]? _icoBytes;

    public TrayIconService(byte[]? icoBytes = null)
    {
        _icoBytes = icoBytes;
        var parameters = new HwndSourceParameters("FT.TraySink") { ParentWindow = new IntPtr(-3) };
        _sink = new HwndSource(parameters);
        _sink.AddHook(WndProc);
    }

    public void Show(string tooltip)
    {
        _icon = _icoBytes is not null ? IconFromIcoBytes(_icoBytes) : IntPtr.Zero;
        if (_icon == IntPtr.Zero) _icon = CreateIcon();
        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _sink.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = tooltip,
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = _data;
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 254 ? text[..254] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added) return;
        _data.szTip = tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    private readonly uint _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // explorer restart deletes every tray icon — re-add ours or the only path to
        // settings/exit disappears (bugbot P2)
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg && _added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            Shell_NotifyIcon(NIM_ADD, ref _data);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_TRAYICON && (long)wParam == 1)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case WM_LBUTTONUP:
                    MainWindowRequested?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP or WM_CONTEXTMENU:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        // Native menu: a WPF ContextMenu on a message-only window is unreliable (never opens),
        // which made the app impossible to close for the user. TrackPopupMenu is the standard.
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, 1001, "设置");
            AppendMenuW(menu, MF_STRING, 1006, "账号");
            AppendMenuW(menu, MF_STRING, 1007, "单词本");
            AppendMenuW(menu, MF_STRING, 1002, _captureEnabled ? "暂停取词" : "恢复取词");
            AppendMenuW(menu, MF_STRING, 1004, "检查更新");
            AppendMenuW(menu, MF_STRING, 1005, "截图翻译");
            AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(menu, MF_STRING, 1003, "退出");

            GetCursorPos(out var pt);
            // classic KB135788 dance so the menu dismisses when clicking elsewhere
            SetForegroundWindow(_sink.Handle);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                pt.X, pt.Y, 0, _sink.Handle, IntPtr.Zero);
            PostMessage(_sink.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1001: SettingsRequested?.Invoke(); break;
                case 1002:
                    _captureEnabled = !_captureEnabled;
                    CaptureToggled?.Invoke(_captureEnabled);
                    break;
                case 1003: ExitRequested?.Invoke(); break;
                case 1004: UpdateRequested?.Invoke(); break;
                case 1005: ScreenshotTranslateRequested?.Invoke(); break;
                case 1006: AccountRequested?.Invoke(); break;
                case 1007: NotesRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>One entry of an ICO container's directory, resolved to its in-buffer payload.</summary>
    internal readonly record struct IcoEntry(int Index, int Width, int DataOffset, int DataLength);

    /// <summary>
    /// Pick the directory entry whose width is closest to <paramref name="targetSize"/>
    /// (a 0 width byte means 256). Returns null for a container that is truncated or whose
    /// chosen payload lies outside the buffer — both used to reach CreateIconFromResourceEx
    /// as garbage, which returns 0 and left the tray with no icon at all.
    /// </summary>
    internal static IcoEntry? PickIcoEntry(byte[] ico, int targetSize)
    {
        if (ico is null || ico.Length < 6 + 16) return null;
        int count = BitConverter.ToUInt16(ico, 4);
        if (count < 1) return null;

        var wanted = targetSize > 0 ? targetSize : 16;
        int best = -1, bestDiff = int.MaxValue;
        for (int i = 0; i < count && 6 + 16 * (i + 1) <= ico.Length; i++)
        {
            int e = 6 + 16 * i;
            int w = ico[e] == 0 ? 256 : ico[e];
            int diff = Math.Abs(w - wanted);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        if (best < 0) return null;

        int entry = 6 + 16 * best;
        int dataLen = (int)BitConverter.ToUInt32(ico, entry + 8);
        int dataOff = (int)BitConverter.ToUInt32(ico, entry + 12);
        if (dataLen <= 0 || dataOff < 6 + 16 * count || dataOff + dataLen > ico.Length) return null;
        return new IcoEntry(best, ico[entry] == 0 ? 256 : ico[entry], dataOff, dataLen);
    }

    private static IntPtr IconFromIcoBytes(byte[] ico)
    {
        // Parse the ICO container manually: pick the directory entry closest to the tray
        // small-icon size and feed its (PNG) payload to CreateIconFromResourceEx — the API
        // takes a single icon image, not the container. Zero extra dependencies (no WinForms).
        [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] presbits, int dwResSize, bool fIcon, int dwVer, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
        try
        {
            int cx = GetSystemMetrics(49); // SM_CXSMICON
            if (cx <= 0) cx = 16;

            if (PickIcoEntry(ico, cx) is not { } picked) return IntPtr.Zero;

            var payload = new byte[picked.DataLength];
            Array.Copy(ico, picked.DataOffset, payload, 0, picked.DataLength);
            return CreateIconFromResourceEx(payload, payload.Length, true, 0x00030000, cx, cx, 0);
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr CreateIcon()
    {
        // 16x16 blue rounded square with "译" — drawn in software (no D3D, M0 D1)
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x62, 0xFF)), null,
                new Rect(0, 0, 16, 16), 4, 4);
            var text = new FormattedText("译",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11, Brushes.White, 1.0);
            dc.DrawText(text, new Point(8 - text.Width / 2, 8 - text.Height / 2));
        }
        var bmp = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        // GDI icon from PNG bytes
        return NativeIconFromPng(ms.ToArray());
    }

    private static IntPtr NativeIconFromPng(byte[] png)
    {
        [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] presbits, int dwResSize, bool fIcon, int dwVer, int cx, int cy, uint LR);
        try { return CreateIconFromResourceEx(png, png.Length, true, 0x00030000, 16, 16, 0); }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _sink.RemoveHook(WndProc);
        _sink.Dispose();
    }
}