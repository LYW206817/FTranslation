namespace FastTranslation.Server;

/// <summary>Bound from the "Server" configuration section (appsettings.json + environment).</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>SQLite file path. Relative paths resolve against the content root.</summary>
    public string DatabasePath { get; set; } = "data/fasttranslation.db";

    /// <summary>Base64 (or plain, with a length warning) HS256 key. Must be at least 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>When false, registration skips the email code entirely (local development).</summary>
    public bool RequireEmailVerification { get; set; }

    public int VerificationCodeMinutes { get; set; } = 10;

    public int VerificationResendSeconds { get; set; } = 60;

    public int VerificationMaxAttempts { get; set; } = 5;

    /// <summary>Shared secret for /api/v1/admin (sent as the X-Admin-Token header). Empty disables the surface.</summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>Per-IP request budget for the auth endpoints, per minute.</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}
