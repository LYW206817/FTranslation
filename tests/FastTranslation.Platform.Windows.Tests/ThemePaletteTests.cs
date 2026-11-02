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
                palette.BrandText, palette.PageFrom, palette.PageTo, palette.PageEnd,
                palette.Glass, palette.GlassEdge, palette.GlowBrand, palette.GlowWarm,
                palette.Warm,
            };
            Assert.Equal(29, values.Length);
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
        // v83：三支强调色各有一套品牌色，任何一个都不许把白字/墨字做到读不清
        foreach (var accent in Accents)
        {
            var palette = ThemePalette.PaletteFor(theme, accent);
            Assert.True(
                Contrast(palette.OnBrandText, palette.Brand) >= 4.5,
                $"{theme}/{accent}: OnBrandText {palette.OnBrandText} 压在 Brand {palette.Brand} 上对比度不足");
        }
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
        foreach (var accent in Accents)
        {
            var palette = ThemePalette.PaletteFor(theme, accent);
            Assert.True(
                Contrast(palette.BrandText, palette.Surface) >= 4.5,
                $"{theme}/{accent}: BrandText {palette.BrandText} 压在 Surface {palette.Surface} 上对比度不足");
            Assert.True(
                Contrast(palette.BrandText, palette.BrandFill) >= 4.5,
                $"{theme}/{accent}: BrandText {palette.BrandText} 压在 BrandFill {palette.BrandFill} 上对比度不足");
        }
    }

    private static readonly AccentTheme[] Accents =
        [AccentTheme.Electro, AccentTheme.Lake, AccentTheme.Violet];

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void EveryAccentHasItsOwnBrand(AppTheme theme)
    {
        var brands = Accents.Select(accent => ThemePalette.PaletteFor(theme, accent).Brand).ToArray();
        Assert.Equal(Accents.Length, brands.Distinct().Count());
    }

    [Theory]
    [InlineData("electro", AccentTheme.Electro)]
    [InlineData("lake", AccentTheme.Lake)]
    [InlineData(" LAKE ", AccentTheme.Lake)]
    [InlineData("violet", AccentTheme.Violet)]
    [InlineData("garbage", AccentTheme.Electro)]
    [InlineData(null, AccentTheme.Electro)]
    [InlineData("", AccentTheme.Electro)]
    public void ResolveAccent_FallsBackToElectro(string? setting, AccentTheme expected)
    {
        Assert.Equal(expected, ThemePalette.ResolveAccent(setting));
        // 写回设置再从设置读出来仍是同一支
        Assert.Equal(expected, ThemePalette.ResolveAccent(ThemePalette.AccentSetting(expected)));
    }

    [Fact]
    public void UseAppliesTheAccentButLeavesTheRestOfThePaletteAlone()
    {
        var originalTheme = ThemePalette.Current;
        var originalAccent = ThemePalette.CurrentAccent;
        try
        {
            ThemePalette.Use(AppTheme.Light, ThemePalette.LakeSetting);
            Assert.Equal(AccentTheme.Lake, ThemePalette.CurrentAccent);
            Assert.NotEqual(ThemePalette.Light.Brand, ThemePalette.Active.Brand);
            Assert.Equal(ThemePalette.Light.Surface, ThemePalette.Active.Surface);
            Assert.Equal(ThemePalette.Light.TextPrimary, ThemePalette.Active.TextPrimary);

            // 电光蓝就是基底调色板，不再复制一遍，因此可以直接比引用
            ThemePalette.Use(AppTheme.Light, ThemePalette.ElectroSetting);
            Assert.Same(ThemePalette.Light, ThemePalette.Active);

            ThemePalette.Use(AppTheme.Dark, ThemePalette.VioletSetting);
            Assert.Equal(AccentTheme.Violet, ThemePalette.CurrentAccent);
            Assert.Same(ThemePalette.Dark.TextPrimary, ThemePalette.Active.TextPrimary);
        }
        finally
        {
            ThemePalette.Use(originalTheme, ThemePalette.AccentSetting(originalAccent));
        }
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
