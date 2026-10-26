using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class HotkeyParsingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", 0x0003u, (uint)'T')]
    [InlineData("ctrl+shift+space", 0x0006u, 0x20u)]
    [InlineData("Win+Enter", 0x0008u, 0x0Du)]
    [InlineData("Ctrl+Tab", 0x0002u, 0x09u)]
    [InlineData("Ctrl+F5", 0x0002u, 0x74u)] // F5 is VK_F5, not the letter F
    [InlineData("Ctrl+D1", 0x0002u, (uint)'1')]
    [InlineData("Alt+9", 0x0001u, (uint)'9')]
    public void TryParse_Accepts(string text, uint modifiers, uint vk)
    {
        Assert.True(HotkeyService.TryParse(text, out var m, out var k));
        Assert.Equal(modifiers, m);
        Assert.Equal(vk, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]          // modifiers only
    [InlineData("T")]             // key only
    [InlineData("Ctrl+NoSuchKey")] // unknown key name
    [InlineData("Ctrl+F25")]      // out of the F1..F24 range
    public void TryParse_Rejects(string text)
    {
        Assert.False(HotkeyService.TryParse(text, out _, out _));
    }
}
