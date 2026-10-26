using Microsoft.Win32;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// Per-user autostart via HKCU\...\Run — no admin, no service, no system-wide state
/// (spec 983: "开机启动用 HKCU Run（应用内开关）"). Portable builds may use it too.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FastTranslation";

    /// <summary>Quoted absolute path of the running executable.</summary>
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

    /// <summary>Writes or deletes the Run value. Returns false when the registry refused.</summary>
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
