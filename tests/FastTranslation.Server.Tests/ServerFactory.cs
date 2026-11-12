using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FastTranslation.Server.Tests;

/// <summary>在进程内启动真实应用,使用隔离的 SQLite 文件与固定签名密钥,所以
/// 端到端测试不依赖开发者本地的数据库或环境。</summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "ft-server-tests",
        Guid.NewGuid().ToString("N") + ".db");

    private readonly string _wechatQrPath = Path.Combine(
        Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + "-wechat.png");

    private readonly string _alipayQrPath = Path.Combine(
        Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + "-alipay.png");

    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";

    public const string AdminToken = "test-admin-token";

    /// <summary>默认放宽管理限流，避免同一测试类的 admin 调用互相挤掉；限流测试单独覆写。</summary>
    protected virtual int AdminRequestsPerMinute => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_wechatQrPath)!);
        File.WriteAllBytes(_wechatQrPath, Convert.FromBase64String(OnePixelPng));
        File.WriteAllBytes(_alipayQrPath, Convert.FromBase64String(OnePixelPng));

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:DatabasePath"] = _databasePath,
            ["Server:SigningKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-signing-key-32b!")),
            ["Server:RequireEmailVerification"] = "false",
            ["Server:AdminToken"] = AdminToken,
            ["Server:AuthRequestsPerMinute"] = "1000",
            ["Server:AdminRequestsPerMinute"] = AdminRequestsPerMinute.ToString(),
            ["Server:AccessTokenMinutes"] = "30",
            ["Server:Payment:WeChatQrPath"] = _wechatQrPath,
            ["Server:Payment:AlipayQrPath"] = _alipayQrPath,
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _databasePath, _wechatQrPath, _alipayQrPath })
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        File.Delete(path + suffix);
                    }
                    catch (IOException)
                    {
                        // 尽力而为地清理。
                    }
                }
            }
        }
    }
}
