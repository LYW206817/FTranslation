using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>对外的小门面，让 App 不必直接接触内部的 P/Invoke 细节。</summary>
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
