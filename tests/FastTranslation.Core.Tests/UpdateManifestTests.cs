using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public class UpdateManifestTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Json(
        string version = "1.1.0",
        string url = "https://example.com/FastTranslation-1.1.0-Setup.exe",
        string sha = Sha,
        string size = "53070995",
        string extra = "")
        => $"{{\"version\":\"{version}\",\"url\":\"{url}\",\"sha256\":\"{sha}\",\"size\":{size},\"notes\":\"hello\"{extra}}}";

    [Fact]
    public void Parse_ReadsAWellFormedManifest()
    {
        Assert.True(UpdateManifest.TryParse(Json(extra: ",\"releasePage\":\"https://github.com/x/y/releases/tag/v1.1.0\""),
            out var manifest, out var error), error);

        Assert.NotNull(manifest);
        Assert.Equal(ReleaseVersion.Parse("1.1.0"), manifest!.Version);
        Assert.Equal("https://example.com/FastTranslation-1.1.0-Setup.exe", manifest.DownloadUrl);
        Assert.Equal(Sha, manifest.Sha256);
        Assert.Equal(53070995, manifest.SizeBytes);
        Assert.Equal("hello", manifest.Notes);
        Assert.Equal("https://github.com/x/y/releases/tag/v1.1.0", manifest.ReleasePageUrl);
    }

    [Fact]
    public void Parse_NormalizesUppercaseHash()
    {
        Assert.True(UpdateManifest.TryParse(Json(sha: Sha.ToUpperInvariant()), out var manifest, out var error), error);
        Assert.Equal(Sha, manifest!.Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    public void Parse_RejectsNonManifest(string json)
    {
        Assert.False(UpdateManifest.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.NotEmpty(error);
    }

    [Theory]
    // version
    [InlineData("version")]
    [InlineData("url")]
    [InlineData("sha256")]
    [InlineData("size")]
    public void Parse_RejectsMissingField(string field)
    {
        var json = Json();
        var start = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = json.IndexOf(',', start);
        if (end < 0) end = json.IndexOf('}', start);
        var broken = json.Remove(start, end - start + (json[end] == ',' ? 1 : 0));
        Assert.False(UpdateManifest.TryParse(broken, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("ftp://example.com/x.exe")]
    [InlineData("https://example.com/payload.zip")]
    [InlineData("/FastTranslation-1.1.0-Setup.exe")]
    [InlineData("not a url")]
    public void Parse_RejectsUnsafeUrls(string url)
    {
        Assert.False(UpdateManifest.TryParse(Json(url: url), out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Parse_RejectsBadHash(string sha)
    {
        Assert.False(UpdateManifest.TryParse(Json(sha: sha), out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"big\"")]
    public void Parse_RejectsBadSize(string size)
    {
        Assert.False(UpdateManifest.TryParse(Json(size: size), out _, out var error));
        Assert.NotEmpty(error);
    }
}
