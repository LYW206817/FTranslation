using System.Windows.Automation;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

public sealed record UiaSelection(
    string Text,
    IReadOnlyList<System.Windows.Rect> Rects,
    long SourceWindowHandle,
    int[]? RuntimeId,
    bool IsWritable);

/// <summary>
/// T10：UIA 选区读取器。M0 的经验都固化在这里：
/// - 一次遍历同时快照文本和矩形(Chromium 会重建自己的树，再次读取会返回 Empty)
/// - 沿父链最多向上找 6 层 TextPattern；Chromium 比较懒——FromPoint 要重试几次
/// - 400ms 预算，超时后调用方回退到剪贴板
/// </summary>
public sealed class UiaSelectionReader
{
    private readonly int _timeoutMs;
    private readonly int _warmupRetries;

    public UiaSelectionReader(int timeoutMs = 400, int warmupRetries = 3)
    {
        _timeoutMs = timeoutMs;
        _warmupRetries = warmupRetries;
    }

    public UiaSelection? ReadAt(ScreenPoint point, string? sourceProcessName = null)
    {
        var deadline = Environment.TickCount64 + _timeoutMs;
        for (var attempt = 0; attempt <= _warmupRetries; attempt++)
        {
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                for (var level = 0; level < 6 && element is not null; level++)
                {
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                        patternObj is TextPattern pattern)
                    {
                        var selection = Snapshot(pattern, element, sourceProcessName);
                        if (selection is not null) return selection;
                    }
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            }
            // ElementNotAvailable 正是重试循环要对付的 Chromium 树重建瞬时错误——
            // 让它继续往下走(bugbot P2：以前这里直接中断了循环)
            catch { /* 瞬时 COM 错误：预算内继续重试 */ }

            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(50);
        }
        return null;
    }


    private static string GetProcessNameFor(long hwnd)
    {
        try
        {
            if (hwnd == 0) return string.Empty;
            NativeMethods.GetWindowThreadProcessId((IntPtr)hwnd, out var pid);
            return pid == 0 ? string.Empty : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 可写性启发式(用户反馈：可编辑字段上必须出现插入/替换按钮)：
    /// 持有者链上任意位置有 Edit(浏览器输入框/textarea/contenteditable
    /// 都会暴露 Edit)，或有一个属于原生编辑器(Word、记事本)的 Document——
    /// 浏览器页面的 Document 并不可写。
    /// </summary>
    /// <summary>
    /// 乐观的可写性判断(用户要求：不要白名单——一切都可翻译、可写)。
    /// 唯一的硬性拒绝是密码字段；真正的可写性在点击时由
    /// WriteBackService 的粘贴后校验来证明(粘贴到只读界面会无事发生，
    /// 在那里降级为复制 + 提示)。
    /// </summary>
    internal static bool IsWritableHolder(AutomationElement holder, string processName)
    {
        try
        {
            var el = holder;
            for (var i = 0; i < 6 && el is not null; i++)
            {
                if (el.Current.IsPassword) return false;
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return true;
    }

    /// <summary>
    /// 粘贴的文本真的落进源窗口的文档里了吗？按窗口句柄判定，
    /// 而不是按屏幕坐标——覆盖住锚点的另一个窗口(或我们自己的浮层)
    /// 不能让校验失真。
    /// </summary>
    public bool TextContainsInWindow(long windowHandle, string substring)
    {
        if (string.IsNullOrEmpty(substring) || windowHandle == 0) return false;
        try
        {
            var rootElement = AutomationElement.FromHandle((IntPtr)windowHandle);
            var docCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
            foreach (AutomationElement doc in rootElement.FindAll(TreeScope.Descendants, docCond))
            {
                if (doc.TryGetCurrentPattern(TextPattern.Pattern, out var po) && po is TextPattern tp)
                {
                    var text = tp.DocumentRange.GetText(200000) ?? string.Empty;
                    if (text.Contains(substring, StringComparison.Ordinal)) return true;
                }
            }
            // 单行输入框(地址栏、表单字段)在 Edit 元素上暴露的是 ValuePattern
            var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            foreach (AutomationElement edit in rootElement.FindAll(TreeScope.Descendants, editCond))
            {
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vo) && vo is ValuePattern vp)
                {
                    if ((vp.Current.Value ?? string.Empty).Contains(substring, StringComparison.Ordinal)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 强制把 UIA 客户端的初始化移出关键路径——否则第一次 FromPoint 会
    /// 加载整套 UIA COM 栈(1-2 秒)，用户的第一次选区只能干等。
    /// </summary>
    public static void Warmup()
    {
        try { _ = AutomationElement.RootElement.Current.ProcessId; }
        catch { }
    }

    /// <summary>触发门控(UI 线程)使用的语义化「可读文本」判断。
    /// 向上走 6 层——Tailwind 风格的页面(Qwen AI 等)会在命中元素和 Document
    /// 之间叠 4 层以上匿名 Group 容器，走得太浅会把真正的文本目标拒掉；
    /// 网页还会覆盖光标形状，使 I-beam 判断不可靠。</summary>
    public static bool LooksTextual(ScreenPoint point)
    {
        // 拖动刚结束时 Chromium 的 FromPoint 回复既慢又不稳定——最多探测 3
        // 次再拒绝(之前一次失手就会让第一次之后的每个选区都失败)
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                for (var level = 0; level < 6 && element is not null; level++)
                {
                    var ct = element.Current.ControlType;
                    if (ct == ControlType.Document || ct == ControlType.Edit) return true;
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            }
            catch { }
            if (attempt < 2) Thread.Sleep(60); // 最后一次尝试后不再等待
        }
        return false;
    }

    private static UiaSelection? Snapshot(TextPattern pattern, AutomationElement element, string? sourceProcessName = null)
    {
        var ranges = pattern.GetSelection();
        if (ranges.Length == 0) return null;

        string text;
        var rects = new List<System.Windows.Rect>();
        try
        {
            text = string.Join("\n", ranges.Select(r => r.GetText(-1)));
            foreach (var r in ranges) rects.AddRange(r.GetBoundingRectangles());
            // 某些页面(Qwen AI 等)上 Chromium 报告了选区矩形却给出空文本，
            // 而这些页面还会吞掉 Ctrl+C，让剪贴板兜底也失效。
            // 于是按几何重建文本：把选区矩形与可见行范围求交，
            // 再按 x 方向的重叠切出每一行已知的文本。
            if (string.IsNullOrEmpty(text) && rects.Count > 0)
            {
                // 宽 1-2px 的矩形只是光标(选区已经塌缩)——
                // 拿它重建只会得到垃圾般的单字符
                var real = rects.Where(r => r.Width > 4).ToList();
                if (real.Count > 0) text = RebuildFromVisibleLines(pattern, real);
            }
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrEmpty(text)) return null; // 退化范围——没有选中任何内容

        long hwnd = 0;
        int[]? runtimeId = null;
        bool writable = false;
        try
        {
            var current = element.Current;
            // 归一到根窗口：GetForegroundWindow 按根窗口比较，而 UIA 的
            // NativeWindowHandle 可能指向内部子窗口(写回校验依赖这一点)
            var raw = current.NativeWindowHandle;
            if (raw != IntPtr.Zero)
            {
                var root = NativeMethods.GetAncestor(raw, 2 /* GA_ROOT：顶层根窗口 */);
                hwnd = (root != IntPtr.Zero ? root : raw).ToInt64();
            }
            // Chromium 元素是无窗口的(NativeWindowHandle=0)——进程名必须
            // 取自触发源的窗口；元素自身有 hwnd 时再回退到它。
            var effectiveProcess = !string.IsNullOrEmpty(sourceProcessName)
                ? sourceProcessName
                : GetProcessNameFor(hwnd);
            writable = IsWritableHolder(element, effectiveProcess);
            runtimeId = element.GetRuntimeId();
        }
        catch { /* 快照过程中元素消失了——上面拿到的矩形/文本仍然有效 */ }

        return new UiaSelection(text, rects, hwnd, runtimeId, writable);
    }

    /// <summary>
    /// 为那些选区范围只有矩形、没有文本的 Chromium 页面重建选区文本：
    /// 按选区覆盖的 x 范围切出每条可见行的可读文本。
    /// 基于行，所以边界处可能有一两个字符偏差——仍然比什么都没有强得多。
    /// </summary>
    /// <summary>
    /// 一行渲染文本中被选区矩形覆盖的部分；该行完全未被选中时返回 null。
    /// 矩形与行的 y 重叠按较小高度的 40% 判定，避免渗入相邻行的矩形
    /// 把那一行也算进来；x 方向与行框求交，再按比例映射为字符下标
    /// (CJK 字形宽度接近等宽，误差控制在一两个字符以内)。
    /// </summary>
    internal static string? SliceLine(System.Windows.Rect lineRect, string lineText, IReadOnlyList<System.Windows.Rect> selRects)
    {
        if (string.IsNullOrEmpty(lineText) || lineRect.Width <= 0) return null;

        double covL = double.MaxValue, covR = double.MinValue;
        foreach (var sr in selRects)
        {
            var yOverlap = Math.Min(sr.Bottom, lineRect.Bottom) - Math.Max(sr.Y, lineRect.Y);
            if (yOverlap <= Math.Min(sr.Height, lineRect.Height) * 0.4) continue;
            covL = Math.Min(covL, Math.Max(sr.X, lineRect.X));
            covR = Math.Max(covR, Math.Min(sr.Right, lineRect.Right));
        }
        if (covL >= covR) return null; // 该行未被选中

        var start = (int)Math.Round((covL - lineRect.X) / lineRect.Width * lineText.Length);
        var end = (int)Math.Ceiling((covR - lineRect.X) / lineRect.Width * lineText.Length);
        start = Math.Clamp(start, 0, lineText.Length);
        end = Math.Clamp(end, 0, lineText.Length);
        if (end <= start) return null;
        return lineText.Substring(start, end - start);
    }

    private static string RebuildFromVisibleLines(TextPattern pattern, List<System.Windows.Rect> selRects)
    {
        try
        {
            var lines = pattern.GetVisibleRanges();
            if (lines.Length == 0) return string.Empty;
            var pieces = new List<(double y, string text)>();
            foreach (var line in lines)
            {
                var lineText = line.GetText(-1);
                if (string.IsNullOrEmpty(lineText)) continue;
                var lineRects = line.GetBoundingRectangles();
                if (lineRects.Length == 0) continue;
                var lr = lineRects[0];
                if (lr.IsEmpty || lr.Width <= 0) continue;

                var slice = SliceLine(lr, lineText, selRects);
                if (string.IsNullOrEmpty(slice)) continue;
                pieces.Add((lr.Y, slice));
            }
            if (pieces.Count == 0) return string.Empty;
            if (WindowsSelectionCapture.DebugEnabled)
            {
                var dbg = $"line-rebuild: selRects=[{string.Join(';', selRects.Select(r => (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height))}] pieces=[{string.Join(" | ", pieces.Select(p => (int)p.y + ":" + p.text))}]";
                WindowsSelectionCapture.Diag(dbg);
            }
            pieces.Sort((a, b) => a.y.CompareTo(b.y));
            return string.Join("\n", pieces.Select(p => p.text));
        }
        catch { return string.Empty; }
    }
}
