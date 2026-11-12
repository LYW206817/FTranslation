using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public sealed class MonitorWorkAreaTests
{
    [Fact]
    public void Placement_converts_a_negative_origin_work_area_to_dip()
    {
        // 副屏在主屏左侧时物理原点为负；换算成 DIP 后必须保留负号，不能夹到 0。
        var placement = new MonitorWorkArea.Placement(new ScreenRect(-1920, 0, 1920, 1040), 1.25, 1.25);

        Assert.Equal(-1536, placement.Left);
        Assert.Equal(0, placement.Top);
        Assert.Equal(1536, placement.Width);
        Assert.Equal(832, placement.Height);
        Assert.Equal(0, placement.Right);
        Assert.Equal(832, placement.Bottom);
    }

    [Fact]
    public void Placement_keeps_separate_horizontal_and_vertical_scales()
    {
        var placement = new MonitorWorkArea.Placement(new ScreenRect(0, 0, 1920, 1080), 1.0, 1.5);

        Assert.Equal(1920, placement.Width);
        Assert.Equal(720, placement.Height);
    }
}
