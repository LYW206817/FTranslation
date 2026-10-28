using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// Paints the native title bar to match the resolved palette (v38). WPF has no managed switch for
/// it: with the palette in dark the client area went dark and the frame stayed light, which is the
/// one piece of chrome the theme could not reach.
/// </summary>
public static class WindowChrome
{
    // 20 is the documented attribute since Windows 10 2004; 19 is the undocumented one 1809–1909
    // used. Trying 20 first and falling back keeps both working without a version check.
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModePre2004 = 19;

    /// <summary>
    /// Call from a window constructor. The HWND does not exist before the window is shown, so the
    /// attribute is applied on SourceInitialized — the first moment DWM will accept it.
    /// </summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try { SetDarkTitleBar(window, ThemePalette.Current == AppTheme.Dark); }
            catch { /* cosmetic only: a stock title bar is not worth losing the window over */ }
        };
    }

    /// <summary>Sets the frame to dark (true) or light (false). False when DWM refused the request.</summary>
    public static bool SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // a forced light theme must also clear a dark system frame, so the value is written both ways
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref value, sizeof(int)) == 0) return true;

        value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, UseImmersiveDarkModePre2004, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
