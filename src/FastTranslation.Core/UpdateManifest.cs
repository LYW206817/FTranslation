using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FastTranslation.Core;

/// <summary>
/// The update manifest served from the public update channel. Every field is validated here
/// because a malformed or hostile manifest must never reach the downloader: the URL decides
/// what gets executed and the hash decides whether it is trusted.
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

    public static bool TryParse(string? json, [NotNullWhen(true)] out UpdateManifest? manifest, out string error)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(json)) { error = "manifest is empty"; return false; }
        if (json.Length > MaxManifestChars) { error = $"manifest is larger than {MaxManifestChars} chars"; return false; }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
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

            manifest = new UpdateManifest(version, url, sha256, size, notes, releasePage);
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
