using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FastTranslation.Server.Tests;

/// <summary>在进程内启动真实应用,使用隔离的 SQLite 文件与固定签名密钥,所以
/// 端到端测试不依赖开发者本地的数据库或环境。</summary>
public sealed class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "ft-server-tests",
        Guid.NewGuid().ToString("N") + ".db");

    public const string AdminToken = "test-admin-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:DatabasePath"] = _databasePath,
            ["Server:SigningKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-signing-key-32b!")),
            ["Server:RequireEmailVerification"] = "false",
            ["Server:AdminToken"] = AdminToken,
            ["Server:AuthRequestsPerMinute"] = "1000",
            ["Server:AccessTokenMinutes"] = "30",
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(_databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }
}
