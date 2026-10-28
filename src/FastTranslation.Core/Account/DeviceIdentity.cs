namespace FastTranslation.Core.Account;

/// <summary>
/// A stable, opaque per-installation id. The server counts free quota per device, so this value
/// must survive restarts (kept in the secret store) but stay unlinked from any hardware id — a
/// machine name or disk serial would be a privacy answer to a quota question.
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

    /// <summary>Display name only — the server stores it so a user can recognise a device later.</summary>
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
