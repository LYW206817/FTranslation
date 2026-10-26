using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>Small public facade so App doesn't touch internal P/Invoke details.</summary>
public static class Win32Facade
{
    public static long GetForegroundWindowHandle() => NativeMethods.GetForegroundWindow().ToInt64();

    public static ScreenPoint PrimaryScreenCenter()
    {
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return new ScreenPoint(w / 2, h / 2);
    }
}
