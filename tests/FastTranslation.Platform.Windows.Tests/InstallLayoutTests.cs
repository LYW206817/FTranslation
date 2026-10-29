using System.Collections.Generic;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class InstallLayoutTests
{
    private static readonly string[] NoProtectedRoots = [];

    [Theory]
    [InlineData(@"C:\Apps\FastTranslation", @"C:\Apps\FastTranslation")]
    [InlineData(@"C:\Apps\FastTranslation\", @"C:\Apps\FastTranslation")]
    [InlineData(@"c:\apps\fasttranslation", @"C:\Apps\FastTranslation")]
    public void IsSameDirectory_IgnoresTrailingSeparatorAndCase(string left, string right) =>
        Assert.True(InstallLayout.IsSameDirectory(left, right));

    [Theory]
    [InlineData(@"C:\Apps\FastTranslation", @"C:\Apps\FastTranslation2")]
    [InlineData(@"C:\Apps\FastTranslation", @"")]
    [InlineData("", @"C:\Apps\FastTranslation")]
    public void IsSameDirectory_RejectsDifferentOrMissingPaths(string left, string right) =>
        Assert.False(InstallLayout.IsSameDirectory(left, right));

    [Fact]
    public void CanSelfUpdate_AllowsTheRegisteredFolder() =>
        Assert.True(InstallLayout.CanSelfUpdate(
            @"D:\FT-Custom-Test", @"D:\FT-Custom-Test", NoProtectedRoots));

    [Fact]
    public void CanSelfUpdate_RejectsAPortableCopyWithNoRecord() =>
        Assert.False(InstallLayout.CanSelfUpdate(@"D:\FastTranslation-portable", null, NoProtectedRoots));

    [Fact]
    public void CanSelfUpdate_RejectsAFolderThatIsNotTheRegisteredOne() =>
        Assert.False(InstallLayout.CanSelfUpdate(
            @"C:\Elsewhere\FastTranslation", @"D:\FT-Custom-Test", NoProtectedRoots));

    [Fact]
    public void CanSelfUpdate_RejectsAFolderUnderProgramFiles()
    {
        // PrivilegesRequired=lowest:在那里静默运行时,应用已经退出之后才会失败
        var roots = new List<string> { @"C:\Program Files", @"C:\Program Files (x86)" };
        Assert.False(InstallLayout.CanSelfUpdate(
            @"C:\Program Files\FastTranslation", @"C:\Program Files\FastTranslation", roots));
    }

    [Fact]
    public void CanSelfUpdate_KeepsSiblingsOfProgramFiles()
    {
        var roots = new List<string> { @"C:\Program Files" };
        Assert.True(InstallLayout.CanSelfUpdate(
            @"C:\Program FilesX\FastTranslation", @"C:\Program FilesX\FastTranslation", roots));
    }

    [Fact]
    public void IsUnder_TreatsTheRootItselfAsProtected()
    {
        var roots = new List<string> { @"C:\Program Files" };
        Assert.True(InstallLayout.IsUnder(@"C:\Program Files", roots));
        Assert.False(InstallLayout.IsUnder(@"D:\Apps", roots));
        Assert.False(InstallLayout.IsUnder(null, roots));
        Assert.False(InstallLayout.IsUnder(@"D:\Apps", null));
    }
}
