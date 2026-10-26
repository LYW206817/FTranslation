using System.Windows;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class UiaLineSliceTests
{
    // a 100px-wide line box holding 10 chars => exactly 10px per character
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
        // x 30..70 of a 10-char line => chars 3..7
        var rects = new[] { new Rect(30, 0, 40, 20) };
        Assert.Equal("defg", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void UnselectedLine_ReturnsNull()
    {
        var rects = new[] { new Rect(0, 60, 100, 20) }; // a different line entirely
        Assert.Null(UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void SlightVerticalBleed_StillCounts()
    {
        // overlaps the line box by 12px, well over the 40% * 20px = 8px threshold
        var rects = new[] { new Rect(0, -8, 100, 20) };
        Assert.Equal("abcdefghij", UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void MarginalVerticalBleed_IsRejected()
    {
        // only 2px of overlap, under the 8px threshold
        var rects = new[] { new Rect(0, -18, 100, 20) };
        Assert.Null(UiaSelectionReader.SliceLine(Line, "abcdefghij", rects));
    }

    [Fact]
    public void SelectionExtendingPastTheLine_ClampsToLineBox()
    {
        // a selection rect dragging past the right edge cannot produce an out-of-range index
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
}
