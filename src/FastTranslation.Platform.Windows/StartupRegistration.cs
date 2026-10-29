using Microsoft.Win32;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 通过 HKCU\...\Run 实现按用户开机自启——不需要管理员权限、不装服务、不产生系统级状态
/// (spec 983：“开机启动用 HKCU Run（应用内开关）”)。便携版同样可以使用。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FastTranslation";

    /// <summary>当前运行中可执行文件的绝对路径(带引号)。</summary>
    public static string Command()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? string.Empty : $"\"{path}\"";
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    /// <summary>写入或删除 Run 值。注册表拒绝操作时返回 false。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var command = Command();
                if (command.Length == 0) return false;
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null) return false;
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}
