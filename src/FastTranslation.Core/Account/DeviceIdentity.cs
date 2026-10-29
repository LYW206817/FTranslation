namespace FastTranslation.Core.Account;

/// <summary>
/// 每次安装一个稳定、不透明的标识。服务端按设备统计免费额度，所以这个值必须能跨重启存活
/// （存在密钥存储里），又不能与任何硬件标识挂钩——拿机器名或磁盘序列号来回答额度问题，
/// 等于把隐私交了出去。
/// </summary>
public static class DeviceIdentity
{
    public const string SecretName = "account-device-id";

    public static string LoadOrCreate(ISecretStore secrets)
    {
        string? existing;
        try { existing = secrets.LoadSecret(SecretName); }
        catch { existing = null; }
        if (!string.IsNullOrWhiteSpace(existing) && Guid.TryParse(existing, out _)) return existing;

        var created = Guid.NewGuid().ToString("N");
        try { secrets.SaveSecret(SecretName, created); }
        catch { }
        return created;
    }

    /// <summary>仅作显示名——服务端存下来，方便用户以后认出是哪台设备。</summary>
    public static string MachineName
    {
        get
        {
            var name = Environment.MachineName?.Trim() ?? string.Empty;
            return name.Length == 0 ? "未命名设备"
                : name.Length <= 64 ? name
                : name[..64];
        }
    }
}
