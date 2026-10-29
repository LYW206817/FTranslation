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
                palette.BrandFillHover, palette.Accent, palette.WindowBg, palette.Surface,
                palette.Hair, palette.HairStrong, palette.BorderStrong, palette.Hover,
                palette.TextPrimary, palette.TextSecondary, palette.TextMuted, palette.Danger,
                palette.Notice,
            };
            Assert.Equal(17, values.Length);
            Assert.All(values, v =>
            {
                Assert.False(string.IsNullOrWhiteSpace(v));
                Assert.StartsWith("#", v);
            });
        }
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
