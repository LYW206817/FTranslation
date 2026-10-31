using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class ThemePaletteTests
{
    [Theory]
    [InlineData("system", false, AppTheme.Light)]
    [InlineData("system", true, AppTheme.Dark)]
    [InlineData("light", true, AppTheme.Light)]
    [InlineData("dark", false, AppTheme.Dark)]
    [InlineData("  DARK  ", false, AppTheme.Dark)]
    [InlineData("Light", false, AppTheme.Light)]
    [InlineData(null, false, AppTheme.Light)]
    [InlineData("", false, AppTheme.Light)]
    [InlineData("garbage", true, AppTheme.Dark)]
    public void Resolve_WithSystemPreference(string? setting, bool systemPrefersDark, AppTheme expected)
    {
        Assert.Equal(expected, ThemePalette.Resolve(setting, systemPrefersDark));
    }

    [Fact]
    public void Active_FollowsCurrent()
    {
        var original = ThemePalette.Current;
        try
        {
            ThemePalette.Use(AppTheme.Dark);
            Assert.Same(ThemePalette.Dark, ThemePalette.Active);

            ThemePalette.Use(AppTheme.Light);
            Assert.Same(ThemePalette.Light, ThemePalette.Active);
        }
        finally
        {
            ThemePalette.Use(original);
        }
    }

    [Fact]
    public void EveryTokenIsFilledInBothThemes()
    {
        foreach (var palette in new[] { ThemePalette.Light, ThemePalette.Dark })
        {
            var values = new[]
            {
                palette.Brand, palette.BrandHover, palette.BrandPressed, palette.BrandFill,
                palette.BrandFillHover, palette.OnBrandText, palette.Accent, palette.WindowBg,
                palette.Surface, palette.Hair, palette.HairStrong, palette.BorderStrong,
                palette.Hover, palette.TextPrimary, palette.TextSecondary, palette.TextMuted,
                palette.Danger, palette.Notice, palette.BoltFrom, palette.BoltTo,
                palette.BrandText,
            };
            Assert.Equal(21, values.Length);
            Assert.All(values, v =>
            {
                Assert.False(string.IsNullOrWhiteSpace(v));
                Assert.StartsWith("#", v);
            });
        }
    }

    /// <summary>
    /// v68：压在品牌色实底上的字必须是可读的。深色主题的品牌蓝偏亮，白字只有
    /// 3.54:1，低于 WCAG AA 的 4.5；这条盯住 OnBrandText 这个令牌别再被改回白。
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void OnBrandTextIsReadableOnTheBrandFill(AppTheme theme)
    {
        var palette = theme == AppTheme.Dark ? ThemePalette.Dark : ThemePalette.Light;
        Assert.True(
            Contrast(palette.OnBrandText, palette.Brand) >= 4.5,
            $"{theme}: OnBrandText {palette.OnBrandText} 压在 Brand {palette.Brand} 上对比度不足");
    }

    /// <summary>
    /// v69：品牌色当文字用的时候要单独一支更浅/更深的色。深色下 Brand 压 Surface
    /// 只有 4.40:1、压侧栏活动项的 BrandFill 只有 3.71:1，正文级文字读不清。
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void BrandTextIsReadableOnSurfaceAndOnBrandFill(AppTheme theme)
    {
        var palette = theme == AppTheme.Dark ? ThemePalette.Dark : ThemePalette.Light;
        Assert.True(
            Contrast(palette.BrandText, palette.Surface) >= 4.5,
            $"{theme}: BrandText {palette.BrandText} 压在 Surface {palette.Surface} 上对比度不足");
        Assert.True(
            Contrast(palette.BrandText, palette.BrandFill) >= 4.5,
            $"{theme}: BrandText {palette.BrandText} 压在 BrandFill {palette.BrandFill} 上对比度不足");
    }

    /// <summary>WCAG 2.x 相对亮度对比度。</summary>
    private static double Contrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var value = Convert.ToInt32(hex[1..], 16);
            var channels = new[] { (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF };
            var linear = channels.Select(c =>
            {
                var s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }).ToArray();
            return (0.2126 * linear[0]) + (0.7152 * linear[1]) + (0.0722 * linear[2]);
        }

        var a = Luminance(foreground);
        var b = Luminance(background);
        return ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05));
    }

    [Fact]
    public void DarkPaletteDiffersFromLightWhereItMatters()
    {
        // 表面与文字必须真正翻转,否则“深色模式”形同虚设
        Assert.NotEqual(ThemePalette.Light.WindowBg, ThemePalette.Dark.WindowBg);
        Assert.NotEqual(ThemePalette.Light.Surface, ThemePalette.Dark.Surface);
        Assert.NotEqual(ThemePalette.Light.TextPrimary, ThemePalette.Dark.TextPrimary);
    }
}
