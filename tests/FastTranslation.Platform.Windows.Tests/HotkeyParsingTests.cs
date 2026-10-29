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
    [InlineData("Ctrl+F5", 0x0002u, 0x74u)] // F5 是 VK_F5,不是字母 F
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
    [InlineData("Ctrl")]          // 只有修饰键
    [InlineData("T")]             // 只有按键
    [InlineData("Ctrl+NoSuchKey")] // 未知按键名
    [InlineData("Ctrl+F25")]      // 超出 F1..F24 范围
    public void TryParse_Rejects(string text)
    {
        Assert.False(HotkeyService.TryParse(text, out _, out _));
    }
}
