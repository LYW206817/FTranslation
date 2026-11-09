using FastTranslation.Server.Auth;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>验证码服务现在有两种用途（注册 / 重置），用途隔离必须在服务层钉住。</summary>
public sealed class VerificationCodeServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly ServerDatabase _database;
    private readonly VerificationCodeService _codes;

    public VerificationCodeServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + ".db");
        _database = new ServerDatabase(_databasePath);
        _database.Initialize();
        var store = new AccountStore(_database);
        _codes = new VerificationCodeService(store, _database, new ServerOptions
        {
            VerificationResendSeconds = 60,
            VerificationCodeMinutes = 10,
            VerificationMaxAttempts = 5,
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
            File.Delete(_databasePath + "-wal");
            File.Delete(_databasePath + "-shm");
        }
        catch (IOException)
        {
            // 尽力而为:残留的临时文件绝不能导致测试运行失败。
        }
    }

    [Fact]
    public void Consume_rejects_a_code_issued_for_another_purpose()
    {
        var issued = _codes.Issue("purpose@example.com", VerificationCodeService.RegisterPurpose);
        Assert.True(issued.Sent);

        Assert.Equal("code_invalid",
            _codes.Consume("purpose@example.com", issued.Code, VerificationCodeService.ResetPurpose));

        // 用途不匹配只记一次失败尝试，不能焚毁原码：注册流程随后仍能用它。
        Assert.Null(_codes.Consume("purpose@example.com", issued.Code, VerificationCodeService.RegisterPurpose));
    }

    [Fact]
    public void Issue_throttles_resend_across_purposes()
    {
        Assert.True(_codes.Issue("throttle@example.com", VerificationCodeService.RegisterPurpose).Sent);

        var reset = _codes.Issue("throttle@example.com", VerificationCodeService.ResetPurpose);

        Assert.False(reset.Sent);
        Assert.Equal("code_recently_sent", reset.Error);
    }
}
