using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// T13：替换式写回，采用 M0 验证过的三重校验：
/// 1. 前台窗口仍然是源窗口(绝不能让第三个应用收到粘贴)
/// 2. 当前键盘焦点仍属于保存了选区的 UIA 元素
/// 3. 选区归一化后的文本仍与捕获时一致
/// 纯文本允许临时粘贴；剪贴板不可暂存时使用 Unicode 输入，校验失败则交由界面提示。
/// 终端/只读/管理员窗口在上游就被拒绝。
/// </summary>
public sealed class WriteBackService : IWriteBack
{
    private const int UnicodeBatchLength = 128;
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
        if (!CanVerifySource(source)) return WriteBackOutcome.AbortedValidation;
        // 等完共用锁再读选区，避免排队期间用户移动光标后仍按旧校验结果写回。
        await ClipboardService.OperationGate.WaitAsync(cancellationToken);
        try { return await ReplaceCoreAsync(source, replacement, cancellationToken); }
        finally { ClipboardService.OperationGate.Release(); }
    }

    private async Task<WriteBackOutcome> ReplaceCoreAsync(
        CapturedSelection source, string replacement, CancellationToken cancellationToken)
    {
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

        if (!SelectionStillMatches(source))
        {
            return WriteBackOutcome.AbortedValidation;
        }

        var outcome = await PasteOrTypeAsync(source, replacement, null, cancellationToken);
        if (outcome != WriteBackOutcome.Replaced) return outcome;
        if (!await VerifyLandedAsync(source.SourceWindowHandle, replacement, cancellationToken))
        {
            return WriteBackOutcome.Failed;
        }
        return WriteBackOutcome.Replaced;
    }

    public async Task<WriteBackOutcome> InsertAsync(CapturedSelection source, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return WriteBackOutcome.Failed;
        if (!CanVerifySource(source)) return WriteBackOutcome.AbortedValidation;
        // 塌缩选区需要与后续粘贴在同一次持锁区间；否则排队会把译文送到新的光标位置。
        await ClipboardService.OperationGate.WaitAsync(cancellationToken);
        try { return await InsertCoreAsync(source, text, cancellationToken); }
        finally { ClipboardService.OperationGate.Release(); }
    }

    private async Task<WriteBackOutcome> InsertCoreAsync(
        CapturedSelection source, string text, CancellationToken cancellationToken)
    {
        Diag("insert: start");
        try
        {
            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle)
            {
                Diag("insert abort: fg=" + NativeMethods.GetForegroundWindow() + " want=" + source.SourceWindowHandle);
                return WriteBackOutcome.AbortedForeground;
            }
        }
        catch { return WriteBackOutcome.AbortedForeground; }

        var expectedCaret = ReadExpectedCaret(source);
        if (expectedCaret is null) return WriteBackOutcome.AbortedValidation;
        if (!TrySelectCaret(expectedCaret))
        {
            if (ValidateInputTarget(source, null) is { } failure) return failure;
            var right = new[]
            {
                KeyInput(0x27, down: true), // VK_RIGHT：provider 不支持选择退化范围时才试方向键。
                KeyInput(0x27, down: false),
            };
            if (NativeMethods.SendInput((uint)right.Length, right, Marshal.SizeOf<NativeMethods.INPUT>()) != right.Length)
            {
                return WriteBackOutcome.Failed;
            }
            await Task.Delay(140, cancellationToken);
        }

        var outcome = await PasteOrTypeAsync(source, text, expectedCaret, cancellationToken);
        if (outcome != WriteBackOutcome.Replaced)
        {
            _log?.Invoke("insert stopped: " + outcome);
            return outcome;
        }

        if (!await VerifyLandedAsync(source.SourceWindowHandle, text, cancellationToken))
        {
            return WriteBackOutcome.Failed;
        }
        return WriteBackOutcome.Inserted;
    }

    private async Task<WriteBackOutcome> PasteOrTypeAsync(
        CapturedSelection source, string text, TextPatternRange? expectedCaret, CancellationToken cancellationToken)
    {
        ClipboardService.TemporaryClipboardText? temporary = null;
        try
        {
            if (ValidateInputTarget(source, expectedCaret) is { } beforeStage) return beforeStage;

            temporary = ClipboardService.TryStageText(text);
            if (temporary is null)
            {
                return await TryUnicodeTypeAsync(source, text, expectedCaret, cancellationToken);
            }

            // 目标程序需要时间看到 OLE 数据；等待期间外部复制或切窗会使原来的粘贴计划失效。
            await Task.Delay(60, cancellationToken);
            if (ValidateInputTarget(source, expectedCaret) is { } afterStage) return afterStage;
            if (!temporary.IsCurrent)
            {
                return WriteBackOutcome.AbortedValidation;
            }

            var keys = new[]
            {
                KeyInput(NativeMethods.VK_CONTROL, down: true),
                KeyInput(NativeMethods.VK_V, down: true),
                KeyInput(NativeMethods.VK_V, down: false),
                KeyInput(NativeMethods.VK_CONTROL, down: false),
            };
            var sent = NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent == keys.Length) return WriteBackOutcome.Replaced;
            // 部分按键可能已经触发粘贴，继续键入会把同一段译文写两遍。
            if (sent != 0) return WriteBackOutcome.Failed;
            return await TryUnicodeTypeAsync(source, text, expectedCaret, cancellationToken);
        }
        finally
        {
            if (temporary is not null)
            {
                await Task.Delay(400, CancellationToken.None);
                temporary.Restore();
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

    private static bool CanVerifySource(CapturedSelection source) =>
        source is { Method: CaptureMethod.UiAutomation, IsWritable: true, SourceElementRuntimeId.Length: > 0 };

    private bool SelectionStillMatches(CapturedSelection source)
    {
        var current = _reader.ReadAt(source.Anchor);
        // UIA 原来能读到的选区消失，常见原因是用户把选区折成了光标。
        return current is { Text.Length: > 0, RuntimeId: { } actual }
            && source.SourceElementRuntimeId is { } expected
            && RuntimeIdEquals(expected, actual)
            && string.Equals(NormalizeForCompare(source.Text), NormalizeForCompare(current.Text), StringComparison.Ordinal);
    }

    private static AutomationElement? FindFocusedTarget(CapturedSelection source)
    {
        if (source.SourceElementRuntimeId is not { } expected) return null;
        try
        {
            // 选区所在的控件可能失焦后仍保留高亮，锚点匹配不足以确认键盘事件的接收控件。
            var element = AutomationElement.FocusedElement;
            for (var level = 0; level < 6 && element is not null; level++)
            {
                if (RuntimeIdEquals(expected, element.GetRuntimeId())) return element;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            Diag("write-back: focused UIA element unavailable");
        }
        return null;
    }

    private static TextPatternRange? ReadExpectedCaret(CapturedSelection source)
    {
        try
        {
            var range = ReadFocusedRange(source);
            if (range is null || !string.Equals(NormalizeForCompare(source.Text), NormalizeForCompare(range.GetText(-1)),
                StringComparison.Ordinal)) return null;
            var end = range.Clone();
            end.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
            return end;
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            Diag("insert: selected UIA range unavailable");
            return null;
        }
    }

    private static TextPatternRange? ReadFocusedRange(CapturedSelection source)
    {
        var target = FindFocusedTarget(source);
        if (target is null || !target.TryGetCurrentPattern(TextPattern.Pattern, out var value)
            || value is not TextPattern pattern) return null;
        return pattern.GetSelection() is [var selection] ? selection : null;
    }

    private static bool TrySelectCaret(TextPatternRange expectedCaret)
    {
        try
        {
            // 双向文本里 Right 是视觉方向，UIA 的范围末尾才是原选区的逻辑末尾。
            expectedCaret.Select();
            return true;
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private WriteBackOutcome? ValidateInputTarget(CapturedSelection source, TextPatternRange? expectedCaret)
    {
        if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle) return WriteBackOutcome.AbortedForeground;
        if (expectedCaret is null)
        {
            return FindFocusedTarget(source) is not null && SelectionStillMatches(source)
                ? null : WriteBackOutcome.AbortedValidation;
        }
        try
        {
            // 只证明选区折叠还不够，用户也可能在原控件里把光标移到了别的字符。
            var actual = ReadFocusedRange(source);
            if (actual is not null
                && actual.CompareEndpoints(TextPatternRangeEndpoint.Start, expectedCaret, TextPatternRangeEndpoint.Start) == 0
                && actual.CompareEndpoints(TextPatternRangeEndpoint.End, expectedCaret, TextPatternRangeEndpoint.End) == 0) return null;
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            Diag("insert: collapsed UIA range unavailable");
        }
        return WriteBackOutcome.AbortedValidation;
    }

    private async Task<WriteBackOutcome> TryUnicodeTypeAsync(
        CapturedSelection source, string text, TextPatternRange? expectedCaret, CancellationToken cancellationToken)
    {
        if (ValidateInputTarget(source, expectedCaret) is { } failure) return failure;
        var inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        for (var offset = 0; offset < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetForegroundWindow() != (IntPtr)source.SourceWindowHandle) return WriteBackOutcome.AbortedForeground;
            if (FindFocusedTarget(source) is null) return WriteBackOutcome.AbortedValidation;

            var length = Math.Min(UnicodeBatchLength, text.Length - offset);
            if (offset + length < text.Length && char.IsSurrogatePair(text, offset + length - 1)) length--;
            var inputs = new NativeMethods.INPUT[length * 2];
            for (var index = 0; index < length; index++)
            {
                var character = text[offset + index];
                inputs[index * 2] = UnicodeKey(character, NativeMethods.KEYEVENTF_UNICODE);
                inputs[index * 2 + 1] = UnicodeKey(character, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
            }
            if (NativeMethods.SendInput((uint)inputs.Length, inputs, inputSize) != inputs.Length) return WriteBackOutcome.Failed;
            offset += length;
            // 大段文本分批交给 Windows，中间让 Dispatcher 处理输入与取消，避免用户切窗时整段继续发送。
            if (offset < text.Length) await Task.Delay(1, cancellationToken);
        }
        return WriteBackOutcome.Replaced;
    }

    private static NativeMethods.INPUT UnicodeKey(char character, uint flags) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wScan = character, dwFlags = flags } },
    };

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
