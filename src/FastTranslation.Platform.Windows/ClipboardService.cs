using System.Runtime.InteropServices;
using Wpf = System.Windows;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T9: clipboard fallback. Sends Ctrl+C to the source window and polls the clipboard
/// sequence number; restores the previous plain-text content afterwards.
/// The caller is responsible for refusing terminal hosts (Ctrl+C = SIGINT).
/// </summary>
public sealed class ClipboardService
{
    private readonly int _pollMs;
    private readonly int _timeoutMs;

    public ClipboardService(int pollMs = 300, int timeoutMs = 1500)
    {
        _pollMs = Math.Clamp(pollMs, 50, 5000);
        _timeoutMs = timeoutMs;
    }

    public string? CaptureViaCopy(long sourceWindowHandle)
    {
        var before = NativeMethods.GetClipboardSequenceNumber();
        string? backup = null;
        try { backup = TryGetText(); } catch { }

        if (!SendCopyToSource(sourceWindowHandle)) return null;

        var deadline = Environment.TickCount64 + _timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(_pollMs);
            if (NativeMethods.GetClipboardSequenceNumber() == before) continue;
            var text = TryGetText();
            if (!string.IsNullOrEmpty(text))
            {
                // success: restore the backup so we leave the user's clipboard intact
                WindowsSelectionCapture.Diag($"clipboard fallback: captured {text.Length}ch");
                if (backup is not null) { try { SetText(backup); } catch { } }
                return text;
            }
        }
        // nothing arrived: restore whatever may have changed, return null
        if (backup is not null)
        {
            try { if (TryGetText() != backup) SetText(backup); } catch { }
        }
        WindowsSelectionCapture.Diag($"clipboard fallback: no clipboard change within timeout (page may block Ctrl+C)");
        return null;
    }

    private static bool SendCopyToSource(long sourceWindowHandle)
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            WindowsSelectionCapture.Diag($"copy fallback: fg=0x{fg.ToInt64():X} src=0x{sourceWindowHandle:X} match={fg == (IntPtr)sourceWindowHandle}");
            if (fg != (IntPtr)sourceWindowHandle) return false;
        }
        catch { return false; }

        // release modifiers so Ctrl+C isn't shadowed by e.g. a lingering Alt
        ReleaseModifier(0x12); // VK_MENU
        ReleaseModifier(0x10); // VK_SHIFT
        ReleaseModifier(0x11); // VK_CONTROL

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

    /// <summary>Snapshot the whole clipboard (all formats) — restoring this preserves text,
    /// HTML, images and files, unlike a text-only backup. Call on an STA thread.</summary>
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

    /// <summary>Send Ctrl+C via SendInput (foreground must already be the source window).</summary>
    public static bool SendCtrlC() => SendCopyToSource(NativeMethods.GetForegroundWindow());

    /// <summary>Poll for new clipboard text via Win32 (no STA/WPF needed — safe on any thread).
    /// Returns null when the sequence number never advances within the timeout.</summary>
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
            var handle = NativeMethods.GetClipboardData(13 /* CF_UNICODETEXT */);
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
        // Clipboard.GetDataObject must run on an STA thread; we are called from the UI thread
        // or a dedicated STA worker. WPF Clipboard retries internally on CLIPBRD_E_CANT_OPEN.
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
