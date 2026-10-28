using System.Text.Json;

namespace FastTranslation.Core.Account;

/// <summary>
/// The signed-in session, persisted through <see cref="ISecretStore"/> (DPAPI on Windows), never
/// through settings.json: the refresh token is a 90-day credential and must not sit in plaintext.
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
            return null; // a corrupt blob must look like "never signed in", not crash startup
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

    /// <summary>Refresh a minute early: a token that expires in flight reads as a sign-out.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt - TimeSpan.FromSeconds(60) <= now;
}
