using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 把原生标题栏绘制成与解析后的调色板一致(v38)，并提供扩展客户区窗口最大化时的非客户区
/// 内缩计算(v189)。WPF 没有托管开关可改原生标题栏：调色板切到深色时客户区变暗，而边框
/// 仍是浅色——这是主题唯一够不到的一块窗口装饰。
/// </summary>
public static class WindowChrome
{
    // 20 是 Windows 10 2004 起有文档的特性值；19 是 1809–1909 使用的未公开值。
    // 先试 20、失败再回退到 19，无需版本判断即可让两者都可用。
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModePre2004 = 19;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const double MaxFrameOverhangDip = 24.0;

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
        // v148：主窗口关到托盘（Hide）期间若系统深浅色切换，恢复显示（Show）时重推一次特性与
        // SWP_FRAMECHANGED，确保隐藏期间没重算的非客户区标题栏立即与当前主题同步。
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            try { SetDarkTitleBar(window, ThemePalette.Current == AppTheme.Dark); }
            catch { /* 同上：标题栏外观失败不应阻断窗口显示 */ }
        };
    }

    /// <summary>
    /// 计算窗口最大化时四边探出显示器工作区的非客户区厚度（DIP，v189）。
    /// 使用 WPF <c>System.Windows.Shell.WindowChrome</c> 扩展客户区后，Windows 在最大化时会把
    /// 窗口矩形向屏外扩出隐藏的缩放边框；不补这段内缩会让侧栏边缘与右上角窗口按钮被屏边裁切。
    /// </summary>
    public static Thickness GetMaximizedPadding(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState != WindowState.Maximized) return new Thickness(0);

        var fallback = SystemParameters.WindowResizeBorderThickness;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var winRect))
        {
            return fallback;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return fallback;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(window).DpiScaleX;
        return ComputeMaximizedPadding(
            info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom,
            winRect.Left, winRect.Top, winRect.Right, winRect.Bottom,
            scale,
            fallback);
    }

    /// <summary>按物理像素工作区与窗口矩形差值换算 DIP 内缩；窗口尚未铺满工作区时退回默认边框厚度。</summary>
    internal static Thickness ComputeMaximizedPadding(
        int workLeft, int workTop, int workRight, int workBottom,
        int winLeft, int winTop, int winRight, int winBottom,
        double scale,
        Thickness fallback)
    {
        if (scale <= 0) return fallback;
        var workWidth = workRight - workLeft;
        var workHeight = workBottom - workTop;
        var winWidth = winRight - winLeft;
        var winHeight = winBottom - winTop;
        if (workWidth <= 0 || workHeight <= 0 || winWidth < workWidth || winHeight < workHeight)
        {
            return fallback;
        }

        var left = Math.Min(MaxFrameOverhangDip, Math.Max(0, workLeft - winLeft) / scale);
        var top = Math.Min(MaxFrameOverhangDip, Math.Max(0, workTop - winTop) / scale);
        var right = Math.Min(MaxFrameOverhangDip, Math.Max(0, winRight - workRight) / scale);
        var bottom = Math.Min(MaxFrameOverhangDip, Math.Max(0, winBottom - workBottom) / scale);
        return new Thickness(left, top, right, bottom);
    }

    /// <summary>把边框设为深色(true)或浅色(false)。DWM 拒绝该请求时返回 false。</summary>
    public static bool SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // 强制浅色主题时也必须清掉深色的系统边框，因此两种取值都要写一遍
        var value = dark ? 1 : 0;
        var applied = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref value, sizeof(int)) == 0;
        if (!applied)
        {
            value = dark ? 1 : 0;
            applied = DwmSetWindowAttribute(hwnd, UseImmersiveDarkModePre2004, ref value, sizeof(int)) == 0;
        }
        if (applied && IsWindowVisible(hwnd))
        {
            // 通知 DWM/非客户区立即重算边框，防止运行中深浅切换后标题栏延迟或未刷新
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        return applied;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
