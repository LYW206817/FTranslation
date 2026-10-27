using System.IO;
using System.Security.Cryptography;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class UpdateInstallerTests
{
    private const string GitHubSetupUrl =
        "https://github.com/LYW206817/FTranslation-Updates/releases/download/v1.1.1/FastTranslation-1.1.1-Setup.exe";

    [Fact]
    public void MirrorCandidates_LeavesGarbageAlone()
    {
        var candidates = UpdateInstaller.MirrorCandidates("not a url");
        Assert.Equal(new[] { "not a url" }, candidates);
    }

    [Fact]
    public void MirrorCandidates_DoesNotProxyANonGitHubUrl()
    {
        // a gh proxy can only fetch from GitHub; prefixing anything else guarantees a failed
        // download where the direct URL would have worked
        var candidates = UpdateInstaller.MirrorCandidates("https://example.com/x.exe");
        Assert.Equal(new[] { "https://example.com/x.exe" }, candidates);
    }

    [Fact]
    public void MirrorCandidates_PutsEveryProxyBeforeTheDirectHost()
    {
        var candidates = UpdateInstaller.MirrorCandidates(GitHubSetupUrl);

        var direct = candidates.ToList().IndexOf(GitHubSetupUrl);
        Assert.True(direct > 0, "the direct URL must not be the first candidate");
        Assert.Equal("https://gh-proxy.com/" + GitHubSetupUrl, candidates[0]);
        Assert.Contains("https://ghfast.top/" + GitHubSetupUrl, candidates);
        Assert.All(candidates.Take(direct), c => Assert.Contains("://gh", c));
    }

    [Fact]
    public void MirrorCandidates_AddsTheWwwHostLastForGitHub()
    {
        // github.com and www.github.com reset differently on some networks, so www is kept as the
        // very last resort — after the direct URL, not before a proxy
        var candidates = UpdateInstaller.MirrorCandidates(GitHubSetupUrl);
        Assert.Equal("https://www.github.com/LYW206817/FTranslation-Updates/releases/download/v1.1.1/FastTranslation-1.1.1-Setup.exe",
            candidates[^1]);
    }

    [Fact]
    public void MirrorCandidates_DoesNotRewriteAnApiGitHubHost()
    {
        // api.github.com answers with metadata, never with the asset, so no proxy applies to it
        const string api = "https://api.github.com/repos/LYW206817/FTranslation-Updates/releases/latest";
        Assert.Equal(new[] { api }, UpdateInstaller.MirrorCandidates(api));
    }

    [Fact]
    public void VerifyDownload_AcceptsMatchingSizeAndHash()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.True(UpdateInstaller.VerifyDownload(path, hash, 7, out var reason), reason);
            Assert.Empty(reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_RejectsWrongSizeBeforeHashing()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.False(UpdateInstaller.VerifyDownload(path, hash, 8, out var reason));
            Assert.Contains("size", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_RejectsWrongHash()
    {
        var path = WriteTemp("payload", out _);
        try
        {
            Assert.False(UpdateInstaller.VerifyDownload(path, new string('0', 64), 7, out var reason));
            Assert.Contains("sha256", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDownload_ReportsAMissingFile()
    {
        Assert.False(UpdateInstaller.VerifyDownload(
            Path.Combine(Path.GetTempPath(), "ft-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            new string('0', 64), 1, out var reason));
        Assert.Contains("missing", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sha256OfFile_IsLowercaseHex()
    {
        var path = WriteTemp("payload", out var hash);
        try
        {
            Assert.Equal(hash, UpdateInstaller.Sha256OfFile(path));
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTemp(string content, out string hash)
    {
        var path = Path.Combine(Path.GetTempPath(), "ft-update-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, content);
        hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return path;
    }
}
