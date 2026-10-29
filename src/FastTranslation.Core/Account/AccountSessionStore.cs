using System.Text.Json;

namespace FastTranslation.Core.Account;

/// <summary>
/// 已登录会话，经 <see cref="ISecretStore"/>（Windows 上是 DPAPI）持久化，绝不走
/// settings.json：refresh token 是 90 天有效的凭据，不能明文躺在磁盘上。
/// </summary>
public sealed class AccountSessionStore
{
    public const string SecretName = "account-session";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ISecretStore _secrets;

    public AccountSessionStore(ISecretStore secrets) => _secrets = secrets;

    public StoredSession? Load()
    {
        string? raw;
        try { raw = _secrets.LoadSecret(SecretName); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(raw, JsonOptions);
            return stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken) ? null : stored;
        }
        catch (JsonException)
        {
            return null; // 损坏的数据块要表现成「从没登录过」，而不是让启动崩溃
        }
    }

    public void Save(AccountSession session)
    {
        var stored = new StoredSession(
            session.AccessToken,
            session.RefreshToken,
            session.ExpiresAt.ToUnixTimeSeconds(),
            session.Profile.Id,
            session.Profile.Email,
            session.Profile.Plan);
        _secrets.SaveSecret(SecretName, JsonSerializer.Serialize(stored, JsonOptions));
    }

    public void Clear()
    {
        try { _secrets.DeleteSecret(SecretName); }
        catch { }
    }
}

public sealed record StoredSession(
    string AccessToken,
    string RefreshToken,
    long ExpiresAtUnix,
    Guid UserId,
    string Email,
    string Plan)
{
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);

    /// <summary>提前一分钟刷新：飞行途中过期的令牌会被当成已退出登录。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt - TimeSpan.FromSeconds(60) <= now;
}
