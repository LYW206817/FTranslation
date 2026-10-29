using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 把原生标题栏绘制成与解析后的调色板一致(v38)。WPF 没有托管开关可做这件事：调色板切到
/// 深色时客户区变暗，而边框仍是浅色——这是主题唯一够不到的一块窗口装饰。
/// </summary>
public static class WindowChrome
{
    // 20 是 Windows 10 2004 起有文档的特性值；19 是 1809–1909 使用的未公开值。
    // 先试 20、失败再回退到 19，无需版本判断即可让两者都可用。
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModePre2004 = 19;

    /// <summary>
    /// 在窗口构造函数里调用。窗口显示之前 HWND 并不存在，所以该特性在 SourceInitialized
    /// 事件里应用——这是 DWM 第一次会接受它的时机。
    /// </summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try { SetDarkTitleBar(window, ThemePalette.Current == AppTheme.Dark); }
            catch { /* 只是外观问题：不值得为一个系统默认标题栏把窗口弄挂 */ }
        };
    }

    /// <summary>把边框设为深色(true)或浅色(false)。DWM 拒绝该请求时返回 false。</summary>
    public static bool SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // 强制浅色主题时也必须清掉深色的系统边框，因此两种取值都要写一遍
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref value, sizeof(int)) == 0) return true;

        value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, UseImmersiveDarkModePre2004, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
