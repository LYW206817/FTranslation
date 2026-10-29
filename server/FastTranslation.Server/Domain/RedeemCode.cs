namespace FastTranslation.Server.Domain;

/// <summary>
/// 激活码。由 16 个 Crockford base32 字符（80 位）组成，形如 FT-XXXX-XXXX-XXXX-XXXX。
/// 字符集去掉了 I、L、O、U，这样从卡片或聊天消息里抄下来的码不会
/// 被误打成数字；而且 <see cref="Normalize"/> 还会把这四个字母折回数字——输入激活码
/// 就是整个购买流程，所以兑换失败只能是码本身错了，绝不能因为大小写而失败。
/// </summary>
public static class RedeemCode
{
    public const int RawLength = 16;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>激活码唯一的生成处。调用方只存格式化后的形式，别的都不存。</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[RawLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        var raw = new char[RawLength];
        for (var i = 0; i < RawLength; i++)
        {
            // 256 是 32 的整数倍，因此掩码取模保持分布均匀，无需拒绝采样。
            raw[i] = Alphabet[bytes[i] & 31];
        }

        return Format(new string(raw));
    }

    public static string Format(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        if (raw.Length != RawLength)
        {
            throw new ArgumentException($"A redeem code has {RawLength} characters, got {raw.Length}.", nameof(raw));
        }

        var builder = new System.Text.StringBuilder("FT", RawLength + 8);
        for (var i = 0; i < RawLength; i += 4)
        {
            builder.Append('-').Append(raw, i, 4);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 接受人类实际输入的形式：任意大小写、有无连字符、有无 FT 前缀，
    /// 并把四个易混字母按形近数字解读。凡是不能
    /// 恰好归一成一份原始码的输入都返回空串，由调用方答复“无效”。
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(input.Length);
        foreach (var character in input.Trim().ToUpperInvariant())
        {
            var folded = character switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                'U' => 'V',
                _ => character,
            };
            if (Alphabet.Contains(folded))
            {
                builder.Append(folded);
            }
        }

        var value = builder.ToString();
        if (value.Length == RawLength + 2 && value.StartsWith("FT", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Length == RawLength ? value : string.Empty;
    }
}
