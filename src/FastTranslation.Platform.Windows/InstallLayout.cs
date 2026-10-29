using System.IO;
using Microsoft.Win32;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 当前运行的副本是否允许原地替换自身。
///
/// 用户在安装向导里选的目录必须在升级后保持不变。Inno 的静默运行通过 UsePreviousAppDir
/// 继承该目录，更新器又以 /DIR 传入同一个值，因此卸载记录是唯一可信来源：不是已注册安装的
/// 副本(解压出来的便携包，或被人手动搬走的文件夹)无法原地替换，只能退回下载页。
/// </summary>
public static class InstallLayout
{
    /// <summary>Inno 按产品分配的卸载键；同一 AppId 的每次安装都相同。</summary>
    public const string UninstallKey = "{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1";

    /// <summary>
    /// 按用户安装绝不能尝试改写的目录：PrivilegesRequired=lowest 意味着
    /// 无法请求提权，静默运行会在应用已经退出之后才失败。
    /// </summary>
    public static IReadOnlyList<string> ProtectedRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    };

    /// <summary>已注册副本的 InstallLocation；本应用从未安装过时为 null。</summary>
    public static string? ReadRegisteredDirectory()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKey}");
                    if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                        return location;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // 读不了的注册表 hive 不可信任；继续找下一个
                }
            }
        }
        return null;
    }

    public static bool CanSelfUpdate(
        string? exeDirectory, string? registeredDirectory, IEnumerable<string>? protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory) || string.IsNullOrWhiteSpace(registeredDirectory))
            return false;
        if (!IsSameDirectory(exeDirectory, registeredDirectory))
            return false;
        return !IsUnder(exeDirectory, protectedRoots);
    }

    public static bool IsSameDirectory(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a is not null && b is not null &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnder(string? directory, IEnumerable<string>? roots)
    {
        var target = Normalize(directory);
        if (target is null || roots is null) return false;
        foreach (var root in roots)
        {
            var parent = Normalize(root);
            if (parent is null) continue;
            if (string.Equals(target, parent, StringComparison.OrdinalIgnoreCase)) return true;
            // 保留分隔符，避免 "C:\Program FilesX" 匹配到 "C:\Program Files"
            if (target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
