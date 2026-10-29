using System.Globalization;
using System.Security.Cryptography;

namespace FastTranslation.Server.Auth;

/// <summary>
/// PBKDF2-SHA256 密码哈希。格式：<c>pbkdf2-sha256$iterations$salt$hash</c>（salt 与 hash 均为 base64）。
/// 迭代次数按记录存储，这样以后可以提高成本而不作废旧记录。
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "iterations must be positive");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Concat(
            Scheme,
            "$",
            iterations.ToString(CultureInfo.InvariantCulture),
            "$",
            Convert.ToBase64String(salt),
            "$",
            Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string stored)
    {
        if (password is null || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme)
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
