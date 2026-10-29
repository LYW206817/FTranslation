using System.IO;
using System.Security.Cryptography;
using System.Text;
using FastTranslation.Core;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// DPAPI(CurrentUser)机密存储——本项目唯一的 NuGet 依赖(ProtectedData)。
/// 文件位于 %LOCALAPPDATA%\FastTranslation；文件丢失只意味着需要重新输入密钥。
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
            return null; // 已损坏或在机器之间迁移过——按缺失处理
        }
    }

    public void DeleteSecret(string name)
    {
        try { File.Delete(PathFor(name)); } catch { }
    }

    private string PathFor(string name) => Path.Combine(_directory, name + ".bin");
}
