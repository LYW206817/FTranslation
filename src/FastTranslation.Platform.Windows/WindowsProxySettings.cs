using System.ComponentModel;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows;

internal readonly record struct WindowsProxySettings(
    bool AutoDetect, string? AutoConfigUrl, string? ProxyServer, string? ProxyBypass)
{
    private const int ErrorFileNotFound = 2;

    public static WindowsProxySettings ReadCurrent()
    {
        var found = WinHttpGetIEProxyConfigForCurrentUser(out var settings);
        var error = Marshal.GetLastPInvokeError();
        try
        {
            if (!found)
            {
                if (error == ErrorFileNotFound) return default;
                throw new HttpRequestException(HttpRequestError.ProxyTunnelError,
                    "Could not read Windows proxy settings.", new Win32Exception(error));
            }

            return new WindowsProxySettings(settings.AutoDetect != 0, Marshal.PtrToStringUni(settings.AutoConfigUrl),
                Marshal.PtrToStringUni(settings.ProxyServer), Marshal.PtrToStringUni(settings.ProxyBypass));
        }
        finally
        {
            // WinHTTP 为每个字符串单独分配内存，结构里的 BOOL 固定占 4 字节。
            if (settings.AutoConfigUrl != 0) _ = GlobalFree(settings.AutoConfigUrl);
            if (settings.ProxyServer != 0) _ = GlobalFree(settings.ProxyServer);
            if (settings.ProxyBypass != 0) _ = GlobalFree(settings.ProxyBypass);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IeProxyConfig
    {
        public int AutoDetect;
        public nint AutoConfigUrl;
        public nint ProxyServer;
        public nint ProxyBypass;
    }

    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IeProxyConfig settings);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalFree(nint memory);
}
