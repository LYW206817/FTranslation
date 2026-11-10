using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class SourceLanguageDetectorTests
{
    [Theory]
    [InlineData("Oi! Como posso ajudar?", "pt", true)]
    [InlineData("The quick brown fox jumps over the lazy dog.", "en", true)]
    [InlineData("你好，世界。今天天气不错。", "zh", true)]
    [InlineData("Hola, ¿cómo puedo ayudarte?", "es", true)]
    [InlineData("Bonjour, comment puis-je vous aider ?", "fr", true)]
    [InlineData("Hallo, wie kann ich Ihnen helfen?", "de", true)]
    [InlineData("Ciao, come posso aiutarti?", "it", true)]
    [InlineData("こんにちは、世界。", "ja", true)]
    [InlineData("안녕하세요 세계.", "ko", true)]
    [InlineData("Привет, как я могу помочь?", "ru", true)]
    public void Detect_RealSentences(string text, string code, bool confident)
    {
        var detection = SourceLanguageDetector.Detect(text);
        Assert.Equal(code, detection.Code);
        Assert.Equal(confident, detection.IsConfident);
    }

    [Theory]
    [InlineData("Olá")]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_ShortOrEmpty_IsNotConfident(string text)
    {
        Assert.False(SourceLanguageDetector.Detect(text).IsConfident);
    }

    [Fact]
    public void DisplaySource_AutoWithConfidentDetection_ShowsRealName()
    {
        Assert.Equal("葡萄牙语", SourceLanguageDetector.DisplaySource("auto", "Oi! Como posso ajudar?"));
    }

    [Fact]
    public void DisplaySource_AutoWithLowConfidence_FallsBackToAutoLabel()
    {
        Assert.Equal("自动检测", SourceLanguageDetector.DisplaySource("auto", "Olá"));
    }

    [Fact]
    public void DisplaySource_ManualPreference_Wins()
    {
        Assert.Equal("葡萄牙语", SourceLanguageDetector.DisplaySource("pt", "hello world"));
    }
}