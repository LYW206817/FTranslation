namespace FastTranslation.Server.Auth;

/// <summary>
/// 认证接口共用的校验。集中在一处，避免注册/登录/重置各写一套而逐渐走样。
/// </summary>
public static class CredentialRules
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 254;

    public static string? ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "email_required";
        }

        var trimmed = email.Trim();
        if (trimmed.Length > MaxEmailLength)
        {
            return "email_too_long";
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0)
        {
            return "email_invalid";
        }

        var domain = trimmed[(at + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return "email_invalid";
        }

        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "password_required";
        }

        if (password.Length < MinPasswordLength)
        {
            return "password_too_short";
        }

        if (password.Length > MaxPasswordLength)
        {
            return "password_too_long";
        }

        return null;
    }
}
