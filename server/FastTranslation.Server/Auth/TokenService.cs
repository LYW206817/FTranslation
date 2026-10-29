using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTranslation.Server.Auth;

/// <summary>
/// 手写的 HS256 JWT（签发 + 校验）。不引 JWT 包：本项目刻意只保留
/// Microsoft.Data.Sqlite 这一个第三方依赖，而 HS256 足够小，自己实现即可。
/// </summary>
public sealed class TokenService
{
    public const string Issuer = "fasttranslation-server";
    public const string Audience = "fasttranslation-client";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly byte[] _signingKey;
    private readonly TimeProvider _time;

    public TokenService(byte[] signingKey, TimeSpan accessTokenLifetime, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("HS256 signing key must be at least 32 bytes", nameof(signingKey));
        }

        if (accessTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(accessTokenLifetime), "lifetime must be positive");
        }

        _signingKey = signingKey;
        AccessTokenLifetime = accessTokenLifetime;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan AccessTokenLifetime { get; }

    public string CreateAccessToken(Guid userId, string email, string plan, out DateTimeOffset expiresAt)
    {
        var now = _time.GetUtcNow();
        expiresAt = now + AccessTokenLifetime;

        var header = Base64Url.EncodeToString("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = userId.ToString("D"),
            ["email"] = email,
            ["plan"] = plan,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        }, JsonOptions));

        var signingInput = header + "." + payload;
        return signingInput + "." + Base64Url.EncodeToString(Sign(signingInput));
    }

    /// <summary>校验签名、签发方、受众与有效期。成功时返回主体信息。</summary>
    public bool TryValidateAccessToken(string? token, out AccessTokenPrincipal principal)
    {
        principal = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        byte[] expected;
        byte[] actual;
        try
        {
            expected = Sign(parts[0] + "." + parts[1]);
            actual = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (GetString(root, "iss") != Issuer || GetString(root, "aud") != Audience)
            {
                return false;
            }

            if (!TryGetInt64(root, "exp", out var exp) || exp <= _time.GetUtcNow().ToUnixTimeSeconds())
            {
                return false;
            }

            if (!Guid.TryParse(GetString(root, "sub"), out var userId))
            {
                return false;
            }

            principal = new AccessTokenPrincipal(
                userId,
                GetString(root, "email") ?? string.Empty,
                GetString(root, "plan") ?? "free",
                exp);
            return true;
        }
    }

    public static string CreateRefreshToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>refresh token 以哈希形式存储，这样数据库泄露也不会直接交出可用会话。</summary>
    public static string HashRefreshToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private byte[] Sign(string signingInput) => HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(signingInput));

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }
}

public readonly record struct AccessTokenPrincipal(Guid UserId, string Email, string Plan, long ExpiresAtUnix);
