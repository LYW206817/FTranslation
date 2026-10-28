using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FastTranslation.Server.Tests;

/// <summary>
/// Boots the real app in-process with an isolated SQLite file and a fixed signing key, so the
/// end-to-end tests do not depend on the developer's local database or environment.
/// </summary>
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
                    // Best effort cleanup.
                }
            }
        }
    }
}
