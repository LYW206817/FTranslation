using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class TextNormalizationTests
{
    [Theory]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("a\r\nb\nc", "a b c")]
    [InlineData("你好\u200B世界", "你好世界")] // 零宽空格(ZWSP)被丢弃(Cf 类)
    [InlineData("　　全角空格", "全角空格")]  // 表意空格被折叠
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_Cases(string input, string expected)
    {
        Assert.Equal(expected, TextNormalization.Normalize(input));
    }

    [Fact]
    public void IsMeaningful_False_WhenOnlyPunctuation()
    {
        Assert.False(TextNormalization.IsMeaningful("...!?"));
        Assert.True(TextNormalization.IsMeaningful("hi"));
    }
}

public class LanguagePolicyTests
{
    [Theory]
    [InlineData("你好世界", "en")]
    [InlineData("hello world", "zh")]
    [InlineData("mixed 中英 text", "zh")]
    public void AutoTarget_Detects(string text, string expectedTarget)
    {
        Assert.Equal(expectedTarget, LanguagePolicy.DetectTarget(text, "auto"));
    }

    [Fact]
    public void FixedTarget_RespectsUser()
    {
        Assert.Equal("ja", LanguagePolicy.DetectTarget("hello", "ja"));
    }

    [Fact]
    public void Resolve_SendsEnglishNames()
    {
        var (source, target) = LanguagePolicy.Resolve("auto", "auto", "你好");
        Assert.Equal(("zh", "en"), (source, target));
    }

    /// <summary>v127: 用户指定目标语言时源语言无从推断，必须留给模型检测。</summary>
    [Fact]
    public void Resolve_WithAnExplicitTarget_LetsTheModelDetectTheSource()
    {
        var (source, target) = LanguagePolicy.Resolve("auto", "ja", "hello world");
        Assert.Equal(("auto", "ja"), (source, target));
    }

    [Fact]
    public void Resolve_KeepsAnExplicitSource()
    {
        var (source, target) = LanguagePolicy.Resolve("en", "zh", "hello");
        Assert.Equal(("en", "zh"), (source, target));
    }
}

public class LanguageCatalogTests
{
    [Fact]
    public void ApiNames_AreEnglish()
    {
        Assert.Equal("Chinese", LanguageCatalog.ToApiName("zh"));
        Assert.Equal("English", LanguageCatalog.ToApiName("en"));
        Assert.Equal("Japanese", LanguageCatalog.ToApiName("ja"));
    }

    [Fact]
    public void Catalog_HasUniqueCodes()
    {
        var codes = LanguageCatalog.Languages.Select(l => l.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DisplayNameForApiName_MapsBackToLabel()
    {
        Assert.Equal("中文(简体)", LanguageCatalog.DisplayNameForApiName("Chinese"));
        Assert.Equal("葡萄牙语", LanguageCatalog.DisplayNameForApiName("Portuguese"));
    }

    [Fact]
    public void DisplayNameForApiName_UnknownFallsBack()
    {
        Assert.Equal("Klingon", LanguageCatalog.DisplayNameForApiName("Klingon"));
    }
}

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ft-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var service = new SettingsService(_dir);
        var settings = new AppSettings { Model = "qwen-mt-plus", Hotkey = "Ctrl+Shift+D", ClipboardFallbackEnabled = false, SoftwareRendering = true };
        service.Save(settings);

        var loaded = new SettingsService(_dir).Load();
        Assert.Equal("qwen-mt-plus", loaded.Model);
        Assert.Equal("Ctrl+Shift+D", loaded.Hotkey);
        Assert.False(loaded.ClipboardFallbackEnabled);
        Assert.True(loaded.SoftwareRendering);
    }

    [Fact]
    public void Software_rendering_defaults_to_off()
    {
        Assert.False(AppSettings.Default().SoftwareRendering);
    }

    [Fact]
    public void Missing_software_rendering_field_falls_back_to_off()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"Model":"qwen-mt-plus"}""");
        var loaded = new SettingsService(_dir).Load();
        Assert.False(loaded.SoftwareRendering);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not valid json !!");
        var loaded = new SettingsService(_dir).Load();
        Assert.Equal("qwen-mt-flash", loaded.Model);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
