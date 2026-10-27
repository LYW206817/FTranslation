using System.IO;
using System.Security.Cryptography;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class UpdateInstallerTests
{
    [Fact]
    public void MirrorCandidates_TriesTheOriginalFirst()
    {
        var candidates = UpdateInstaller.MirrorCandidates("https://example.com/x.exe");
        Assert.Equal("https://example.com/x.exe", candidates[0]);
    }

    [Fact]
    public void MirrorCandidates_AddsTheProxyForAnyAbsoluteUrl()
    {
        var candidates = UpdateInstaller.MirrorCandidates("https://example.com/x.exe");
        Assert.Contains("https://gh-proxy.com/https://example.com/x.exe", candidates);
    }

    [Fact]
    public void MirrorCandidates_AddsTheWwwHostForGitHub()
    {
        // github.com/reset-point behaviour differs between www and bare hosts on some networks
        var candidates = UpdateInstaller.MirrorCandidates(
            "https://github.com/LYW206817/FTranslation-Updates/releases/download/v1.1.0/a.exe");
        Assert.Contains(candidates, c => c.StartsWith("https://www.github.com/", StringComparison.Ordinal));
    }

    [Fact]
    public void MirrorCandidates_LeavesGarbageAlone()
    {
        var candidates = UpdateInstaller.MirrorCandidates("not a url");
        Assert.Equal(new[] { "not a url" }, candidates);
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
