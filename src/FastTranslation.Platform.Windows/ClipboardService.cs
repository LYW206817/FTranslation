using System.Runtime.InteropServices;
using Wpf = System.Windows;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T9：剪贴板兜底方案。向源窗口发送 Ctrl+C，并轮询剪贴板的
/// 序列号；之后恢复原来的纯文本内容。
/// 拒绝终端类宿主是调用方的责任(Ctrl+C = SIGINT)。
/// </summary>
public static class ClipboardService
{
    internal static SemaphoreSlim OperationGate { get; } = new(1, 1);

    private static bool SendCopyToSource(long sourceWindowHandle)
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            WindowsSelectionCapture.Diag($"copy fallback: fg=0x{fg.ToInt64():X} src=0x{sourceWindowHandle:X} match={fg == (IntPtr)sourceWindowHandle}");
            if (fg != (IntPtr)sourceWindowHandle) return false;
        }
        catch { return false; }

        // 先松开修饰键，避免 Ctrl+C 被残留的 Alt 等键吞掉
        ReleaseModifier(0x12); // VK_MENU(Alt 键)
        ReleaseModifier(0x10); // VK_SHIFT(Shift 键)
        ReleaseModifier(0x11); // VK_CONTROL(Ctrl 键)

        var inputs = new[]
        {
            Input.Key(NativeMethods.VK_CONTROL, down: true),
            Input.Key(NativeMethods.VK_C, down: true),
            Input.Key(NativeMethods.VK_C, down: false),
            Input.Key(NativeMethods.VK_CONTROL, down: false),
        };
        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Length;
    }

    private static void ReleaseModifier(ushort vk)
    {
        var up = Input.Key(vk, down: false);
        NativeMethods.SendInput(1, new[] { up }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>对整个剪贴板做快照(所有格式)——恢复它才能保住文本、
    /// HTML、图片和文件，纯文本备份做不到。请在 STA 线程上调用。</summary>
    public static System.Windows.IDataObject? SnapshotClipboard()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return Wpf.Clipboard.GetDataObject(); }
            catch (COMException) when (attempt < 2) { Thread.Sleep(40); }
            catch { return null; }
        }
        return null;
    }

    public static void RestoreClipboard(System.Windows.IDataObject? data)
    {
        if (data is null) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Wpf.Clipboard.SetDataObject(data); return; }
            catch (COMException) when (attempt < 2) { Thread.Sleep(40); }
        }
    }

    public static uint GetSequence() => NativeMethods.GetClipboardSequenceNumber();

    internal static TemporaryClipboardText? TryStageText(string text)
    {
        var before = GetSequence();
        try
        {
            var source = Wpf.Clipboard.GetDataObject();
            var formats = source?.GetFormats(autoConvert: false) ?? [];
            if (formats.Any(format => !IsPlainTextFormat(format)))
            {
                return null;
            }

            var previous = source?.GetData(Wpf.DataFormats.UnicodeText) as string;
            if ((formats.Length > 0 && previous is null) || GetSequence() != before)
            {
                return null;
            }

            var owner = new Wpf.DataObject(Wpf.DataFormats.UnicodeText, text);
            Wpf.Clipboard.SetDataObject(owner);
            return new TemporaryClipboardText(owner, previous, GetSequence());
        }
        catch (ExternalException)
        {
            // 剪贴板正被别的进程使用时，写回仍可走无需暂存的 Unicode 输入。
            return null;
        }
    }

    internal static bool IsPlainTextFormat(string format) =>
        format is "UnicodeText" or "Text" or "OEMText" or "System.String" or "Locale";

    internal sealed class TemporaryClipboardText(Wpf.DataObject owner, string? previous, uint sequence)
    {
        internal bool IsCurrent => GetSequence() == sequence && Wpf.Clipboard.IsCurrent(owner);

        internal void Restore()
        {
            try
            {
                // v201: 延时期间用户可能已重新复制。检查 OLE 所有权，避免把新内容覆盖掉。
                if (!IsCurrent) return;
                if (previous is null) Wpf.Clipboard.Clear();
                else Wpf.Clipboard.SetText(previous);
            }
            catch (ExternalException)
            {
                // 其他进程正在操作剪贴板时不再重试覆盖，用户的新操作优先。
            }
        }
    }

    /// <summary>通过 SendInput 发送 Ctrl+C(前台窗口必须已经是源窗口)。</summary>
    public static bool SendCtrlC() => SendCopyToSource(NativeMethods.GetForegroundWindow());

    /// <summary>用 Win32 轮询新的剪贴板文本(不需要 STA/WPF——任意线程都安全)。
    /// 超时内序列号始终没有前进时返回 null。</summary>
    public static string? PollNewTextWin32(int timeoutMs, uint beforeSeq)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(120);
            if (NativeMethods.GetClipboardSequenceNumber() == beforeSeq) continue;
            var text = TryGetTextWin32();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private static string? TryGetTextWin32()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = NativeMethods.GetClipboardData(13 /* CF_UNICODETEXT，Unicode 文本格式 */);
            if (handle == IntPtr.Zero) return null;
            var ptr = NativeMethods.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { NativeMethods.GlobalUnlock(handle); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    public static string? TryGetText()
    {
        // Clipboard.GetDataObject 必须在 STA 线程上运行；本方法由 UI 线程
        // 或专用 STA 工作线程调用。WPF 的 Clipboard 在 CLIPBRD_E_CANT_OPEN 时会内部重试。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Wpf.Clipboard.ContainsText()) return null;
                var text = Wpf.Clipboard.GetText(Wpf.TextDataFormat.UnicodeText);
                return text;
            }
            catch (COMException) when (attempt < 2)
            {
                Thread.Sleep(40);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static void SetText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Wpf.Clipboard.SetText(text); return; }
            catch (COMException) when (attempt < 2) { Thread.Sleep(40); }
        }
    }
}

internal static class Input
{
    public static NativeMethods.INPUT Key(ushort vk, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
            },
        },
    };
}
