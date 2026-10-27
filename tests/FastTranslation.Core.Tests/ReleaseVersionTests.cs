using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.1.0", 1, 1, 0, 0)]
    [InlineData("1.1", 1, 1, 0, 0)]
    [InlineData(" v2.0.3 ", 2, 0, 3, 0)]
    [InlineData("1.0.1.4", 1, 0, 1, 4)]
    [InlineData("1.1.0-beta.2+abc123", 1, 1, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30, 0)]
    public void Parse_ReadsNumericFields(string text, int major, int minor, int patch, int revision)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.0")]
    [InlineData("1..2")]
    [InlineData("1.2.")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.3.4.")]
    public void Parse_RejectsGarbage(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(ReleaseVersion.None, version);
    }

    [Fact]
    public void Compare_IsNumericNotLexical()
    {
        Assert.True(ReleaseVersion.Parse("1.10.0").IsNewerThan(ReleaseVersion.Parse("1.9.9")));
        Assert.True(ReleaseVersion.Parse("1.0.1").IsNewerThan(ReleaseVersion.Parse("1.0.0")));
        Assert.True(ReleaseVersion.Parse("1.0.0.1").IsNewerThan(ReleaseVersion.Parse("1.0.0")));
        Assert.False(ReleaseVersion.Parse("1.0.0").IsNewerThan(ReleaseVersion.Parse("1.0.0")));
        Assert.False(ReleaseVersion.Parse("1.0.0").IsNewerThan(ReleaseVersion.Parse("1.0.0.1")));
    }

    [Fact]
    public void ToString_DropsZeroRevision()
    {
        Assert.Equal("1.1.0", ReleaseVersion.Parse("1.1.0.0").ToString());
        Assert.Equal("1.1.0.2", ReleaseVersion.Parse("1.1.0.2").ToString());
    }
}
