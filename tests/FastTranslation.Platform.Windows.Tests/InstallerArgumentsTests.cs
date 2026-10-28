using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class InstallerArgumentsTests
{
    [Fact]
    public void BuildInstallerArguments_AlwaysCarriesTheUnattendedSwitches()
    {
        var arguments = UpdateInstaller.BuildInstallerArguments(4321, null);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID=4321", arguments);
    }

    [Fact]
    public void BuildInstallerArguments_RepeatsTheInstallFolder()
    {
        // the wizard's folder page never shows in silent mode, so /DIR is the only way the
        // profile keeps the location the user picked
        var arguments = UpdateInstaller.BuildInstallerArguments(4321, @"D:\FT-Custom-Test");
        Assert.Equal(
            "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID=4321 /DIR=D:\\FT-Custom-Test",
            arguments);
    }

    [Fact]
    public void BuildInstallerArguments_QuotesAFolderWithSpaces()
    {
        var arguments = UpdateInstaller.BuildInstallerArguments(7, @"D:\My Apps\FastTranslation");
        Assert.EndsWith("/DIR=\"D:\\My Apps\\FastTranslation\"", arguments);
    }
}
