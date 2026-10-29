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
        // 静默模式下向导的目录页从不显示,所以 /DIR 是
        // profile 保留用户所选位置的唯一途径
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
