using System.Windows;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class UiaLineSliceTests
{
    // 100px 宽的行框容纳 10 个字符 => 每个字符正好 10px
    private static readonly Rect Line = new(0, 0, 100, 20);

    [Fact]
    public void FullLineSelected_ReturnsWholeText()
    {
        var rects = new[] { new Rect(0, 0, 100, 20) };
        Assert.Equal("abcdefghij", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void HalfLineSelected_ReturnsApproximateHalf()
    {
        var rects = new[] { new Rect(0, 0, 50, 20) };
        Assert.Equal("abcde", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void MiddleSpanSelected_ReturnsInnerSlice()
    {
        // 10 字符行的 x 30..70 => 字符 3..7
        var rects = new[] { new Rect(30, 0, 40, 20) };
        Assert.Equal("defg", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void UnselectedLine_ReturnsNull()
    {
        var rects = new[] { new Rect(0, 60, 100, 20) }; // 完全不同的另一行
        Assert.Null(UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void SlightVerticalBleed_StillCounts()
    {
        // 与行框重叠 12px,远超 40% * 20px = 8px 的阈值
        var rects = new[] { new Rect(0, -8, 100, 20) };
        Assert.Equal("abcdefghij", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void MarginalVerticalBleed_IsRejected()
    {
        // 只重叠 2px,低于 8px 阈值
        var rects = new[] { new Rect(0, -18, 100, 20) };
        Assert.Null(UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void SelectionExtendingPastTheLine_ClampsToLineBox()
    {
        // 拖过右边缘的选区矩形不能产生越界索引
        var rects = new[] { new Rect(-20, 0, 400, 20) };
        Assert.Equal("abcdefghij", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void EmptyLineOrZeroWidth_ReturnsNull()
    {
        Assert.Null(UiaSelectionReader.SliceLine(Line, "", new[] { new Rect(0, 0, 100, 20) }));
        Assert.Null(UiaSelectionReader.SliceLine(new Rect(0, 0, 0, 20), "abc", new[] { new Rect(0, 0, 100, 20) }));
    }

    [Fact]
    public void NoSelectionRects_ReturnsNull()
    {
        Assert.Null(UiaSelectionReader.SliceLine(Line, "abcdefghij", Array.Empty<Rect>()));
    }

    [Fact]
    public void MultilineAccessibilityHint_IsNotRebuiltAsOneSelectedLine()
    {
        // v206：Monaco 会把含换行的无障碍说明放进 TextPattern 的可见范围，
        // 其行框又与真实编辑器选区重合；按单行比例切它必然得到错误源文本。
        var hint = "现在无法访问编辑器。\n若要启用屏幕阅读器优化模式，请使用 Shift+Alt+F1。";
        var editorSelection = new Rect(895, 883, 1499, 34);
        Assert.Null(UiaSelectionReader.SliceLine(editorSelection, hint, new[] { editorSelection }));
    }
}
