using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class FtThemeTests
{
    [Fact]
    public void Refresh_AfterStylesSealedAndOverlaysConstructed_DoesNotThrowAndUpdatesFrozenBrushes()
    {
        RunInSta(() =>
        {
            var originalTheme = ThemePalette.Current;
            var originalAccent = ThemePalette.CurrentAccent;
            try
            {
                ThemePalette.Use(AppTheme.Light, ThemePalette.ElectroSetting);
                FtTheme.Refresh();

                // 模拟 WPF 首次使用 Style 时自动 Seal：v144 把可变画刷直接写进 Setter/Trigger，
                // Seal 后在首次 FtTheme.Refresh() 时会抛 InvalidOperationException。
                foreach (var style in new[] { FtTheme.Primary(), FtTheme.Ghost(), FtTheme.PillGhost(), FtTheme.RoundGhost() })
                {
                    style.Seal();
                    Assert.True(style.IsSealed);
                }

                using var toolbar = new SelectionToolbar();
                using var card = new ResultCardWindow();
                using var image = new ImageResultWindow();

                ThemePalette.Use(AppTheme.Dark, ThemePalette.VioletSetting);
                toolbar.RefreshTheme();
                card.RefreshTheme();
                image.RefreshTheme();

                AssertBrushMatches(FtTheme.Keys.Brand, ThemePalette.Active.Brand);
                AssertBrushMatches(FtTheme.Keys.Surface, ThemePalette.Active.Surface);
                AssertBrushMatches(FtTheme.Keys.TextPrimary, ThemePalette.Active.TextPrimary);

                ThemePalette.Use(AppTheme.Light, ThemePalette.LakeSetting);
                FtTheme.Refresh();

                AssertBrushMatches(FtTheme.Keys.Brand, ThemePalette.Active.Brand);
                AssertBrushMatches(FtTheme.Keys.Surface, ThemePalette.Active.Surface);
                AssertBrushMatches(FtTheme.Keys.TextPrimary, ThemePalette.Active.TextPrimary);
            }
            finally
            {
                ThemePalette.Use(originalTheme, ThemePalette.AccentSetting(originalAccent));
                FtTheme.Refresh();
            }
        });
    }

    [Fact]
    public void SetDarkTitleBar_WithoutHandle_ReturnsFalse()
    {
        RunInSta(() =>
        {
            var window = new Window();
            Assert.False(WindowChrome.SetDarkTitleBar(window, dark: true));
            Assert.False(WindowChrome.SetDarkTitleBar(window, dark: false));
            window.Close();
        });
    }

    [Fact]
    public void GetMaximizedPadding_NormalWindow_ReturnsZero()
    {
        RunInSta(() =>
        {
            var window = new Window { WindowState = WindowState.Normal };
            Assert.Equal(new Thickness(0), WindowChrome.GetMaximizedPadding(window));
            window.Close();
        });
    }

    [Fact]
    public void ComputeMaximizedPadding_ConvertsPixelOverhangByDpi()
    {
        var fallback = new Thickness(4);
        // 150% DPI 下 12px 隐藏缩放边框等于 8 DIP
        var padded = WindowChrome.ComputeMaximizedPadding(
            workLeft: 0, workTop: 0, workRight: 2520, workBottom: 1596,
            winLeft: -12, winTop: -12, winRight: 2532, winBottom: 1608,
            scale: 1.5,
            fallback);
        Assert.Equal(new Thickness(8, 8, 8, 8), padded);

        // 未铺满工作区时回退默认值
        var unpositioned = WindowChrome.ComputeMaximizedPadding(
            0, 0, 2520, 1596,
            100, 100, 1420, 960,
            1.5,
            fallback);
        Assert.Equal(fallback, unpositioned);

        // 异常大偏移钳制到上限，避免整窗内容被推进中间
        var clamped = WindowChrome.ComputeMaximizedPadding(
            0, 0, 1920, 1040,
            -100, -100, 2020, 1140,
            1.0,
            fallback);
        Assert.Equal(new Thickness(24, 24, 24, 24), clamped);
    }

    private static void AssertBrushMatches(string key, string expectedHex)
    {
        var brush = Assert.IsType<SolidColorBrush>(FtTheme.Resources[key]);
        Assert.True(brush.IsFrozen);
        var expectedColor = (Color)ColorConverter.ConvertFromString(expectedHex);
        Assert.Equal(expectedColor, brush.Color);
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
