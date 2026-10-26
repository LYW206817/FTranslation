using System.Runtime.InteropServices;
using System.Windows.Automation;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T13: replace-writeback with the M0-proven triple validation:
/// 1. foreground is still the source window (a third app must never receive the paste)
/// 2. UIA RuntimeId of the element at the anchor still matches
/// 3. the selection's normalized text still matches what we captured
/// Paste via Ctrl+V; if clipboard staging fails or validation aborts → copy translation
/// to the clipboard instead. Terminals/read-only/admin windows are refused upstream.
/// </summary>
public sealed class WriteBackService : IWriteBack
{
    private readonly UiaSelectionReader _reader;
    private readonly Action<string>? _log;

    public WriteBackService(UiaSelectionReader reader, Action<string>? log = null)
    {
        _reader = reader;
        _log = log;
    }

    public async Task<WriteBackOutcome> ReplaceAsync(CapturedSelection source, string replacement, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(replacement)) return WriteBackOutcome.Failed;

        // Validation 1: foreground window is the source
        try
        {
            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle)
            {
                Diag("replace abort: fg=" + NativeMethods.GetForegroundWindow() + " want=" + source.SourceWindowHandle);
                return WriteBackOutcome.AbortedForeground;
            }
        }
        catch { return WriteBackOutcome.AbortedForeground; }
        Diag("replace: start");

        // Validation 2+3: same element (RuntimeId) and same selection text at the anchor.
        // When UIA cannot re-read the selection (VSCode/Monaco-class editors), degrade to
        // foreground-only validation instead of refusing — the user explicitly clicked and
        // the paste still lands in the still-selected source window.
        var current = _reader.ReadAt(source.Anchor);
        var canVerifyText = current is not null && !string.IsNullOrEmpty(current.Text);
        if (canVerifyText)
        {
            var expected = NormalizeForCompare(source.Text);
            var actual = NormalizeForCompare(current!.Text);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                Diag("replace abort: selection changed");
                return Aborted(source);
            }
            if (source.SourceElementRuntimeId is not null && current.RuntimeId is not null &&
                !RuntimeIdEquals(source.SourceElementRuntimeId, current.RuntimeId))
            {
                Diag("replace abort: runtime id changed");
                return Aborted(source);
            }
        }
        else
        {
            Diag("replace: fg-only validation");
        }

        // Paste: stage clipboard, then Ctrl+V with an explicit MOVE first
        // (M0: DOWN/UP without MOUSEEVENTF_MOVE ignore dx/dy).
        // The user's clipboard is restored on EVERY exit path (bugbot P1: aborts used to
        // leave the translation on the clipboard, losing the original content).
        string? backup = null;
        try
        {
            try { backup = ClipboardService.TryGetText(); } catch { }
            ClipboardService.SetText(replacement);
            await Task.Delay(60, cancellationToken);

            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle)
            {
                return WriteBackOutcome.AbortedForeground;
            }

            var focusPoint = source.Anchor;
            var inputs = new List<NativeMethods.INPUT>
            {
                KeyInput(NativeMethods.VK_CONTROL, down: true),
                KeyInput(NativeMethods.VK_V, down: true),
                KeyInput(NativeMethods.VK_V, down: false),
                KeyInput(NativeMethods.VK_CONTROL, down: false),
            };
            var sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Count;
            await Task.Delay(200, cancellationToken);

            if (!sent)
            {
                // Unicode SendInput fallback, typed character by character
                sent = TryUnicodeType(replacement);
            }
            if (!sent) return WriteBackOutcome.Failed;

            // Post-paste verification (optimistic buttons): if the pasted text did NOT land —
            // read-only surfaces no-op the paste — degrade to copy instead of pretending.
            if (source.Method == CaptureMethod.UiAutomation && canVerifyText)
            {
                var landed = await VerifyLandedAsync(source.SourceWindowHandle, replacement, cancellationToken);
                Diag("replace: landed=" + landed);
                if (!landed) return WriteBackOutcome.Failed;
            }

            return WriteBackOutcome.Replaced;
        }
        finally
        {
            if (backup is not null)
            {
                try { await Task.Delay(400, CancellationToken.None); ClipboardService.SetText(backup); } catch { }
            }
        }
    }

    public async Task<WriteBackOutcome> InsertAsync(CapturedSelection source, string text, CancellationToken cancellationToken)
    {
        Diag("insert: start");
        if (string.IsNullOrEmpty(text)) return WriteBackOutcome.Failed;

        try
        {
            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle)
            {
                Diag("insert abort: fg=" + NativeMethods.GetForegroundWindow() + " want=" + source.SourceWindowHandle);
                return WriteBackOutcome.AbortedForeground;
            }
        }
        catch { return WriteBackOutcome.AbortedForeground; }

        // validate when UIA can read; otherwise proceed with fg-only validation (VSCode-class)
        var current = _reader.ReadAt(source.Anchor);
        var canVerifyText = current is not null && !string.IsNullOrEmpty(current.Text);
        if (canVerifyText && current is not null)
        {
            if (!string.Equals(NormalizeForCompare(source.Text), NormalizeForCompare(current.Text), StringComparison.Ordinal))
            {
                Diag("insert abort: text mismatch");
                return Aborted(source);
            }
            Diag("insert: validation ok");
        }
        else
        {
            Diag("insert: selection not verifiable via UIA (fg-only validation)");
        }

        var right = new[]
        {
            KeyInput(0x27, down: true), // VK_RIGHT: collapse selection to its end
            KeyInput(0x27, down: false),
        };
        NativeMethods.SendInput((uint)right.Length, right, Marshal.SizeOf<NativeMethods.INPUT>());
        await Task.Delay(140, cancellationToken);

        // after collapse the selection must be degenerate; a non-empty read means the editor
        // did not behave as expected — abort rather than paste into the wrong place
        var collapsed = _reader.ReadAt(source.Anchor);
        Diag("insert: after collapse read=" + (collapsed is null ? "null" : (collapsed.Text.Length + "ch")));
        if (collapsed is not null && !string.IsNullOrEmpty(collapsed.Text))
        {
            _log?.Invoke("insert abort: selection did not collapse");
            return Aborted(source);
        }

        // clipboard restored on every exit path (bugbot P1, same as replace)
        string? backup = null;
        try
        {
            try { backup = ClipboardService.TryGetText(); } catch { }
            ClipboardService.SetText(text);
            await Task.Delay(60, cancellationToken);

            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle)
            {
                Diag("insert abort: fg changed before paste fg=" + NativeMethods.GetForegroundWindow() + " want=" + source.SourceWindowHandle);
                return WriteBackOutcome.AbortedForeground;
            }

            var paste = new List<NativeMethods.INPUT>
            {
                KeyInput(NativeMethods.VK_CONTROL, down: true),
                KeyInput(NativeMethods.VK_V, down: true),
                KeyInput(NativeMethods.VK_V, down: false),
                KeyInput(NativeMethods.VK_CONTROL, down: false),
            };
            var sent = NativeMethods.SendInput((uint)paste.Count, paste.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>()) == paste.Count;
            Diag("insert: paste sent=" + sent);
            if (!sent) sent = TryUnicodeType(text);
            if (!sent) return WriteBackOutcome.Failed;

            // Post-paste verification, same optimistic contract as replace
            if (source.Method == CaptureMethod.UiAutomation && canVerifyText)
            {
                var landed = await VerifyLandedAsync(source.SourceWindowHandle, text, cancellationToken);
                Diag("insert: landed=" + landed);
                if (!landed) return WriteBackOutcome.Failed;
            }

            return WriteBackOutcome.Inserted;
        }
        finally
        {
            if (backup is not null)
            {
                try { await Task.Delay(400, CancellationToken.None); ClipboardService.SetText(backup); } catch { }
            }
        }
    }

    /// <summary>UIA text can lag the paste by hundreds of ms — retry briefly before failing.</summary>
    private async Task<bool> VerifyLandedAsync(long windowHandle, string pastedText, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(250, ct);
            if (_reader.TextContainsInWindow(windowHandle, pastedText)) return true;
        }
        return false;
    }

    // user direction: failures must NOT touch the clipboard — the card notice explains it.
    private static WriteBackOutcome Aborted(CapturedSelection source) => WriteBackOutcome.AbortedValidation;

    private static bool TryUnicodeType(string text)
    {
        try
        {
            foreach (var ch in text)
            {
                var down = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wScan = (ushort)ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE } },
                };
                var up = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wScan = (ushort)ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } },
                };
                if (NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<NativeMethods.INPUT>()) != 2) return false;
                Thread.Sleep(2);
            }
            return true;
        }
        catch { return false; }
    }

    private static NativeMethods.INPUT KeyInput(ushort vk, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP } },
    };

    private static bool RuntimeIdEquals(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Diag(string message) => WindowsSelectionCapture.Diag(message);

    internal static string NormalizeForCompare(string text)
    {
        var normalized = TextNormalization.Normalize(text);
        return normalized.Replace("\r", "").Replace("\n", " ").Trim();
    }
}
