using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class WriteBackValidationTests
{
    [Theory]
    [InlineData("hello world", "hello  world")]     // whitespace collapse
    [InlineData("a\r\nb", "a b")]                  // newlines read back as spaces
    [InlineData("  trim me  ", "trim me")]
    [InlineData("你好\u200B世界", "你好世界")]        // zero-width dropped
    public void NormalizeForCompare_EquatesReadBackNoise(string captured, string reread)
    {
        Assert.Equal(
            WriteBackService.NormalizeForCompare(captured),
            WriteBackService.NormalizeForCompare(reread));
    }

    [Fact]
    public void NormalizeForCompare_KeepsRealDifferences()
    {
        Assert.NotEqual(
            WriteBackService.NormalizeForCompare("hello"),
            WriteBackService.NormalizeForCompare("hallo"));
    }

    [Fact]
    public void RuntimeIdEquals_ComparesElementIdentity()
    {
        Assert.True(WriteBackService.RuntimeIdEquals(new[] { 42, 1, 0 }, new[] { 42, 1, 0 }));
        Assert.False(WriteBackService.RuntimeIdEquals(new[] { 42, 1, 0 }, new[] { 42, 2, 0 }));
        Assert.False(WriteBackService.RuntimeIdEquals(new[] { 42, 1 }, new[] { 42, 1, 0 }));
        Assert.True(WriteBackService.RuntimeIdEquals(Array.Empty<int>(), Array.Empty<int>()));
    }
}
