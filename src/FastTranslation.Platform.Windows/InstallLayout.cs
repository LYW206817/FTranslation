using System.IO;
using Microsoft.Win32;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// Whether the running copy is allowed to replace itself in place.
///
/// The folder the user picked in the setup wizard has to survive an upgrade. Inno's silent run
/// inherits it through UsePreviousAppDir and the updater passes the same value as /DIR, so the
/// uninstall record is the single source of truth: a copy that is not the registered install (an
/// extracted portable zip, or a folder someone moved by hand) cannot be replaced and has to fall
/// back to the download page.
/// </summary>
public static class InstallLayout
{
    /// <summary>Inno's per-product uninstall key; the same for every install of this AppId.</summary>
    public const string UninstallKey = "{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1";

    /// <summary>
    /// Directories a per-user install must never try to rewrite: PrivilegesRequired=lowest means
    /// there is no elevation to ask for, so the silent run would fail after the app already exited.
    /// </summary>
    public static IReadOnlyList<string> ProtectedRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    };

    /// <summary>InstallLocation of the registered copy, or null when this app was never installed.</summary>
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
                    // a hive we cannot read is a hive we cannot trust; keep looking
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
            // the separator keeps "C:\Program FilesX" from matching "C:\Program Files"
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
