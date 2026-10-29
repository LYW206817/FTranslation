namespace FastTranslation.Server.Domain;

/// <summary>
/// Activation codes. 16 characters of Crockford base32 (80 bits) printed as FT-XXXX-XXXX-XXXX-XXXX.
/// The alphabet leaves out I, L, O and U so a code read off a card or out of a chat message cannot
/// be mistyped as a digit, and <see cref="Normalize"/> folds those four anyway — typing the code is
/// the entire purchase flow, so a failed redemption has to be a wrong code, never a wrong capital.
/// </summary>
public static class RedeemCode
{
    public const int RawLength = 16;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The only place a code is born. Callers store the formatted form and nothing else.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[RawLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        var raw = new char[RawLength];
        for (var i = 0; i < RawLength; i++)
        {
            // 256 is a multiple of 32, so masking keeps the draw uniform without rejection sampling.
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
    /// Accepts what a human types: any case, with or without dashes, with or without the FT prefix,
    /// and with the four ambiguous letters read as the digits they look like. Anything that does not
    /// collapse to exactly one raw code comes back empty, and the caller answers "invalid".
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
