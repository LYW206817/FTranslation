using System.Runtime.InteropServices;
using System.Windows.Automation;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T13：替换式写回，采用 M0 验证过的三重校验：
/// 1. 前台窗口仍然是源窗口(绝不能让第三个应用收到粘贴)
/// 2. 锚点处元素的 UIA RuntimeId 仍然一致
/// 3. 选区归一化后的文本仍与捕获时一致
/// 经 Ctrl+V 粘贴；若剪贴板暂存失败或校验中止 → 改为把译文复制到剪贴板。
/// 终端/只读/管理员窗口在上游就被拒绝。
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

        // 校验 1：前台窗口就是源窗口
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

        // 校验 2+3：锚点处是同一个元素(RuntimeId)、选中文本也相同。
        // 当 UIA 无法重新读到选区时(VSCode/Monaco 这类编辑器)，降级为只校验前台，
        // 而不是直接拒绝——用户是显式点击的，
        // 粘贴仍然落在这个依旧选中着文本的源窗口里。
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

        // 粘贴：先把译文暂存到剪贴板，然后发 Ctrl+V，且先显式 MOVE 一次
        // (M0：不带 MOUSEEVENTF_MOVE 的 DOWN/UP 会忽略 dx/dy)。
        // 用户的剪贴板在每一条退出路径上都会被还原(bugbot P1：以前中止时
        // 会把译文留在剪贴板里，原内容就丢了)。
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
                // Unicode SendInput 兜底：逐字符输入
                sent = TryUnicodeType(replacement);
            }
            if (!sent) return WriteBackOutcome.Failed;

            // 粘贴后校验(乐观按钮)：如果粘贴的文本并没有落进去——
            // 只读界面会静默忽略粘贴——就降级为复制，而不是假装成功。
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

        // UIA 能读到时校验；否则带着仅前台校验继续(VSCode 这类)
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
            KeyInput(0x27, down: true), // VK_RIGHT：把选区塌缩到末尾
            KeyInput(0x27, down: false),
        };
        NativeMethods.SendInput((uint)right.Length, right, Marshal.SizeOf<NativeMethods.INPUT>());
        await Task.Delay(140, cancellationToken);

        // 塌缩后选区必须是退化的；还能读到非空文本说明编辑器
        // 没有按预期行动——宁可中止也不要粘错位置
        var collapsed = _reader.ReadAt(source.Anchor);
        Diag("insert: after collapse read=" + (collapsed is null ? "null" : (collapsed.Text.Length + "ch")));
        if (collapsed is not null && !string.IsNullOrEmpty(collapsed.Text))
        {
            _log?.Invoke("insert abort: selection did not collapse");
            return Aborted(source);
        }

        // 每条退出路径都还原剪贴板(bugbot P1，与 replace 相同)
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

            // 粘贴后校验，契约与 replace 相同
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

    /// <summary>UIA 文本可能比粘贴晚几百毫秒——失败前先短暂重试。</summary>
    private async Task<bool> VerifyLandedAsync(long windowHandle, string pastedText, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(250, ct);
            if (_reader.TextContainsInWindow(windowHandle, pastedText)) return true;
        }
        return false;
    }

    // 用户方向：失败绝不能动剪贴板——由卡片上的提示来解释。
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

    internal static bool RuntimeIdEquals(int[] a, int[] b)
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
