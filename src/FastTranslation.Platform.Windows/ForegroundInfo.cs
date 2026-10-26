using System.Diagnostics;

namespace FastTranslation.Platform.Windows;

/// <summary>Process name of the current foreground window — powers the settings window's
/// "add foreground app to exclusions" shortcut (spec 723).</summary>
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
