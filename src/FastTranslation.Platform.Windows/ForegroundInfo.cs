using System.Diagnostics;

namespace FastTranslation.Platform.Windows;

/// <summary>当前前台窗口的进程名——用于设置窗口里「把前台应用加入排除列表」的快捷入口(spec 723)。</summary>
public static class ForegroundInfo
{
    public static string ProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return string.Empty;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return string.Empty; }
    }
}
