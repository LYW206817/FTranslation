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
/// T10: UIA selection reader. M0 lessons baked in:
/// - snapshot text+rects in one pass (Chromium rebuilds its tree; re-reads return Empty)
/// - walk up to 6 parents for TextPattern; Chromium is lazy — retry FromPoint a few times
/// - 400ms budget, then the caller falls back to clipboard
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
            // ElementNotAvailable is exactly the transient Chromium-tree-rebuild error the
            // retry loop exists for — falling through (bugbot P2: it used to abort the loop)
            catch { /* transient COM errors: retry while budget remains */ }

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
    /// Writability heuristic (user feedback: insert/replace buttons must appear for editable
    /// fields): an Edit anywhere on the holder chain (browser inputs/textarea/contenteditable
    /// expose Edit), or a Document belonging to a native editor (Word, Notepad) — a browser's
    /// page Document is NOT writable.
    /// </summary>
    /// <summary>
    /// Optimistic writability (user direction: no whitelists — everything translatable and
    /// writable). The only hard refusals are password fields; real editability is proven at
    /// click time by post-paste verification in WriteBackService (paste into a read-only
    /// surface no-ops there and degrades to copy + notice).
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
    /// Did the pasted text really land in the SOURCE window's document? Resolves by window
    /// handle, not by screen point — another window covering the anchor (or our own overlays)
    /// must not poison the verification.
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
            // single-line inputs (address bars, form fields) expose ValuePattern on Edit elements
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
    /// Force UIA client initialization off the critical path — the first FromPoint otherwise
    /// loads the whole UIA COM stack (1-2s) while the user's first selection waits.
    /// </summary>
    public static void Warmup()
    {
        try { _ = AutomationElement.RootElement.Current.ProcessId; }
        catch { }
    }

    /// <summary>Semantic text-capable check used by the trigger gate (UI thread).
    /// Walks 6 levels — Tailwind-style pages (Qwen AI etc.) stack 4+ anonymous Group
    /// containers between the hit element and the Document, so a shallow walk rejects
    /// real text targets; web pages also override the cursor, making I-beam unreliable.</summary>
    public static bool LooksTextual(ScreenPoint point)
    {
        // Chromium answers FromPoint lazily/unstably right after a drag — probe up to 3
        // times before rejecting (a single miss killed every selection after the first)
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
            if (attempt < 2) Thread.Sleep(60); // no sleep after the final attempt
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
            // Chromium on some pages (Qwen AI etc.) reports selection rects but EMPTY text
            // for selections, and those same pages swallow Ctrl+C, killing the clipboard
            // fallback. Rebuild the text geometrically: intersect selection rects with the
            // visible line ranges and slice each line's known text by x-overlap.
            if (string.IsNullOrEmpty(text) && rects.Count > 0)
            {
                // a 1-2px wide rect is just the caret (selection already collapsed) —
                // rebuilding from it produces garbage single characters
                var real = rects.Where(r => r.Width > 4).ToList();
                if (real.Count > 0) text = RebuildFromVisibleLines(pattern, real);
            }
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrEmpty(text)) return null; // degenerate range — nothing selected

        long hwnd = 0;
        int[]? runtimeId = null;
        bool writable = false;
        try
        {
            var current = element.Current;
            // normalize to the root window: GetForegroundWindow compares roots, and UIA's
            // NativeWindowHandle may point at an inner child (write-back validation depends on this)
            var raw = current.NativeWindowHandle;
            if (raw != IntPtr.Zero)
            {
                var root = NativeMethods.GetAncestor(raw, 2 /* GA_ROOT */);
                hwnd = (root != IntPtr.Zero ? root : raw).ToInt64();
            }
            // Chromium elements are windowless (NativeWindowHandle=0) — the process name must
            // come from the trigger's source window; fall back to the element hwnd when present.
            var effectiveProcess = !string.IsNullOrEmpty(sourceProcessName)
                ? sourceProcessName
                : GetProcessNameFor(hwnd);
            writable = IsWritableHolder(element, effectiveProcess);
            runtimeId = element.GetRuntimeId();
        }
        catch { /* element vanished mid-snapshot — rects/text above remain valid */ }

        return new UiaSelection(text, rects, hwnd, runtimeId, writable);
    }

    /// <summary>
    /// Rebuild selection text for Chromium pages whose selection ranges carry rects but no
    /// text: slice each visible line's readable text by the x-range the selection covers.
    /// Line-based, so a few boundary characters may be off — still far better than nothing.
    /// </summary>
    /// <summary>
    /// The part of one rendered line that the selection rects cover, or null when the line is
    /// not selected at all. Rect y-overlap is judged at 40% of the smaller height so a rect
    /// bleeding into an adjacent line does not claim it; x is intersected against the line box
    /// and then mapped to a character index proportionally (CJK glyphs are near-uniform, so
    /// the error stays within a character or two).
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
        if (covL >= covR) return null; // line not selected

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
