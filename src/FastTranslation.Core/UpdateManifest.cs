using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace FastTranslation.Core;

/// <summary>
/// 公共更新通道下发的更新清单。所有字段都在这里校验，因为格式错误或恶意的清单绝不能
/// 流到下载器；格式校验通过后仍须单独验证发布者签名。
/// </summary>
public sealed record UpdateManifest(
    ReleaseVersion Version,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string Notes,
    string ReleasePageUrl)
{
    public const int MaxManifestChars = 64 * 1024;
    public const int MaxNotesLength = 2000;
    public string Signature { get; init; } = string.Empty;

    /// <summary>发布工具与客户端使用同一组已规范化字段生成签名数据。</summary>
    public byte[] GetSigningPayload() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "fasttranslation.update.v1",
        version = Version.ToString(),
        url = DownloadUrl,
        sha256 = Sha256,
        size = SizeBytes,
        notes = Notes,
        releasePage = ReleasePageUrl,
    });

    /// <summary>只有嵌入构建的发布公钥能授权自更新，空公钥降级为手动下载。</summary>
    public bool VerifySignature(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(Signature)
            || !Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            using var verifier = ECDsa.Create();
            var key = Convert.FromBase64String(publicKey);
            verifier.ImportSubjectPublicKeyInfo(key, out var read);
            return read == key.Length && verifier.KeySize == 256
                && verifier.VerifyData(GetSigningPayload(), Convert.FromBase64String(Signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // 发布字段损坏时仍允许通过固定入口手动更新。
            return false;
        }
    }

    public static bool TryParse(string? json, [NotNullWhen(true)] out UpdateManifest? manifest, out string error)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(json)) { error = "manifest is empty"; return false; }
        if (json.Length > MaxManifestChars) { error = $"manifest is larger than {MaxManifestChars} chars"; return false; }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            error = $"manifest is not valid JSON ({ex.Message})";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "manifest root is not an object"; return false; }

            if (!TryReadString(root, "version", out var versionText, out error)) return false;
            if (!ReleaseVersion.TryParse(versionText, out var version))
            {
                error = $"version '{versionText}' is not major.minor[.patch[.revision]]";
                return false;
            }

            if (!TryReadString(root, "url", out var url, out error)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = $"url '{url}' is not an absolute http(s) URL";
                return false;
            }
            if (!uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = $"url '{url}' does not point at a .exe";
                return false;
            }

            if (!TryReadString(root, "sha256", out var sha256, out error)) return false;
            sha256 = sha256.Trim().ToLowerInvariant();
            if (sha256.Length != 64 || !IsHex(sha256))
            {
                error = "sha256 must be 64 hex characters";
                return false;
            }

            if (!root.TryGetProperty("size", out var sizeElement) ||
                sizeElement.ValueKind != JsonValueKind.Number ||
                !sizeElement.TryGetInt64(out var size) ||
                size <= 0)
            {
                error = "'size' must be a positive byte count";
                return false;
            }

            var notes = ReadOptionalString(root, "notes");
            if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];

            var releasePage = ReadOptionalString(root, "releasePage");
            if (releasePage.Length > 0 &&
                (!Uri.TryCreate(releasePage, UriKind.Absolute, out var pageUri) ||
                 (pageUri.Scheme != Uri.UriSchemeHttps && pageUri.Scheme != Uri.UriSchemeHttp)))
            {
                error = $"'releasePage' '{releasePage}' is not an absolute http(s) URL";
                return false;
            }

            manifest = new UpdateManifest(version, url, sha256, size, notes, releasePage)
            {
                Signature = ReadOptionalString(root, "signature"),
            };
            error = string.Empty;
            return true;
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string value, out string error)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = $"'{name}' is missing or not a string";
            return false;
        }
        value = (element.GetString() ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = $"'{name}' is empty";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static string ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? (element.GetString() ?? string.Empty).Trim()
            : string.Empty;

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
