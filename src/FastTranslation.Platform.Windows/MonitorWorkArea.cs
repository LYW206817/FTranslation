using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 目标显示器的工作区（物理像素）与缩放比。WPF 的 Left/Top 是 DIP，多屏且缩放比不同时
/// 只按主屏工作区算会把窗口放错屏幕或被夹回主屏，所以位置计算统一走这里。
/// </summary>
public static class MonitorWorkArea
{
    public readonly record struct Placement(ScreenRect WorkPhysical, double ScaleX, double ScaleY)
    {
        public double Left => WorkPhysical.X / ScaleX;

        public double Top => WorkPhysical.Y / ScaleY;

        public double Width => WorkPhysical.Width / ScaleX;

        public double Height => WorkPhysical.Height / ScaleY;

        public double Right => (WorkPhysical.X + WorkPhysical.Width) / ScaleX;

        public double Bottom => (WorkPhysical.Y + WorkPhysical.Height) / ScaleY;
    }

    public static Placement ForPoint(ScreenPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = point.X, Y = point.Y },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromMonitor(monitor, IntPtr.Zero);
    }

    /// <summary>按物理像素直接定位窗口（不改尺寸、不激活）。返回 false 表示句柄尚未创建。</summary>
    public static bool SetPhysicalPosition(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        return true;
    }

    public static Placement ForWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return ForPoint(WindowsSelectionCapture.CurrentCursorPoint());
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromMonitor(monitor, hwnd);
    }

    private static Placement FromMonitor(IntPtr monitor, IntPtr window)
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // 取不到显示器信息时退回主屏工作区；此时按 96 DPI 处理，至少不会把窗口丢到屏幕外。
            var area = SystemParameters.WorkArea;
            return new Placement(
                new ScreenRect((int)area.Left, (int)area.Top, (int)area.Width, (int)area.Height),
                1.0, 1.0);
        }

        var dpiX = 96u;
        var dpiY = 96u;
        var windowDpi = window == IntPtr.Zero ? 0u : NativeMethods.GetDpiForWindow(window);
        if (windowDpi > 0)
        {
            dpiX = windowDpi;
            dpiY = windowDpi;
        }
        else if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var monitorX, out var monitorY) == 0
                 && monitorX > 0 && monitorY > 0)
        {
            dpiX = monitorX;
            dpiY = monitorY;
        }

        var work = info.rcWork;
        return new Placement(
            new ScreenRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
            dpiX / 96.0,
            dpiY / 96.0);
    }
}
