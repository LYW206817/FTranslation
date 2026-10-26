using System.IO;
using System.Security.Cryptography;
using System.Text;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// DPAPI (CurrentUser) secret store — the only NuGet dependency (ProtectedData).
/// The file sits in %LOCALAPPDATA%\FastTranslation; losing it just means re-entering the key.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    public DpapiSecretStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation");
    }

    public void SaveSecret(string name, string value)
    {
        Directory.CreateDirectory(_directory);
        var entropy = Encoding.UTF8.GetBytes("ft-" + name);
        var plain = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), protectedBytes);
    }

    public string? LoadSecret(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            var entropy = Encoding.UTF8.GetBytes("ft-" + name);
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null; // corrupted or moved between machines — treat as missing
        }
    }

    public void DeleteSecret(string name)
    {
        try { File.Delete(PathFor(name)); } catch { }
    }

    private string PathFor(string name) => Path.Combine(_directory, name + ".bin");
}
