using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FastTranslation.Server.Storage;

namespace FastTranslation.Server.Auth;

/// <summary>
/// 签发并校验 6 位注册验证码。验证码以哈希形式存储、会过期，且
/// 用后即焚；重发有节流，避免该接口被当作邮件轰炸工具。
/// </summary>
public sealed class VerificationCodeService(AccountStore store, ServerDatabase database, ServerOptions options)
{
    public sealed record IssueResult(bool Sent, string? Error, int RetryAfterSeconds, string? Code);

    public IssueResult Issue(string email)
    {
        var normalized = AccountStore.NormalizeEmail(email);
        var now = database.NowUnix;

        if (store.FindVerificationCode(normalized) is { } record)
        {
            var elapsed = now - record.LastSentAt;
            if (elapsed < options.VerificationResendSeconds)
            {
                return new IssueResult(false, "code_recently_sent", (int)(options.VerificationResendSeconds - elapsed), null);
            }
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        store.SaveVerificationCode(normalized, HashCode(normalized, code), now + (options.VerificationCodeMinutes * 60L), now);
        return new IssueResult(true, null, 0, code);
    }

    /// <summary>验证码有效时返回 null（并焚毁它），否则返回稳定的错误标记。</summary>
    public string? Consume(string email, string? code)
    {
        var normalized = AccountStore.NormalizeEmail(email);
        var record = store.FindVerificationCode(normalized);
        if (record is null)
        {
            return "code_missing";
        }

        if (record.ExpiresAt <= database.NowUnix)
        {
            store.DeleteVerificationCode(normalized);
            return "code_expired";
        }

        if (record.Attempts >= options.VerificationMaxAttempts)
        {
            store.DeleteVerificationCode(normalized);
            return "code_attempts_exceeded";
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            store.IncrementVerificationAttempts(normalized);
            return "code_invalid";
        }

        var expected = HashCode(normalized, code.Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(record.CodeHash)))
        {
            store.IncrementVerificationAttempts(normalized);
            return "code_invalid";
        }

        store.DeleteVerificationCode(normalized);
        return null;
    }

    private static string HashCode(string email, string code)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email + ":" + code)));
}
