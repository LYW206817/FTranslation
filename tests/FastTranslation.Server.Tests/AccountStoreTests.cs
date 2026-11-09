using FastTranslation.Server.Auth;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>针对一次性文件演练真实 SQLite schema——不用内存里的假实现,因为
/// 额度 upsert 与设备上限恰恰是假实现会掩盖的部分。</summary>
public sealed class AccountStoreTests : IDisposable
{
    private readonly string _databasePath;
    private readonly ServerDatabase _database;
    private readonly AccountStore _store;

    public AccountStoreTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + ".db");
        _database = new ServerDatabase(_databasePath);
        _database.Initialize();
        _store = new AccountStore(_database);
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

    private UserRecord CreateUser(string email = "user@example.com")
        => _store.CreateUser(email, "hash", PlanTier.Free);

    [Fact]
    public void CreateUser_NormalizesEmailAndRejectsDuplicates()
    {
        var user = _store.CreateUser("  Mixed.Case@Example.COM ", "hash", PlanTier.Free);

        Assert.Equal("mixed.case@example.com", user.Email);
        Assert.Equal(PlanTier.Free, user.Plan);
        Assert.NotNull(_store.FindUserByEmail("mixed.case@example.com"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => _store.CreateUser("MIXED.CASE@example.com", "hash", PlanTier.Free));
    }

    [Fact]
    public void SetPlan_RoundTrips()
    {
        var user = CreateUser();
        Assert.Equal(PlanTier.Free, _store.FindUserById(user.Id)!.Plan);

        Assert.True(_store.SetPlan(user.Id, PlanTier.Standard));
        Assert.Equal(PlanTier.Standard, _store.FindUserById(user.Id)!.Plan);
    }

    [Fact]
    public void RegisterDevice_ReusesRowForSameFingerprint()
    {
        var user = CreateUser("dev@example.com");

        var first = _store.RegisterDevice(user.Id, "fp-1", "laptop");
        var second = _store.RegisterDevice(user.Id, "fp-1", "laptop-renamed");

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Device!.Id, second.Device!.Id);
        Assert.Equal("laptop-renamed", second.Device!.Name);
    }

    [Fact]
    public void RegisterDevice_EnforcesDeviceCap()
    {
        var user = CreateUser("cap@example.com");
        for (var i = 0; i < Plans.MaxDevicesPerAccount; i++)
        {
            Assert.True(_store.RegisterDevice(user.Id, $"fp-{i}", "pc").Succeeded);
        }

        var overflow = _store.RegisterDevice(user.Id, "fp-overflow", "pc");

        Assert.False(overflow.Succeeded);
        Assert.Equal("device_limit_reached", overflow.Error);
        Assert.Null(overflow.Device);
    }

    [Fact]
    public void ListDevices_ReturnsOnlyThisAccountsDevices()
    {
        var owner = CreateUser("devices@example.com");
        var other = CreateUser("other-devices@example.com");
        var first = _store.RegisterDevice(owner.Id, "fp-a", "pc-a").Device!;
        var second = _store.RegisterDevice(owner.Id, "fp-b", "pc-b").Device!;
        _store.RegisterDevice(other.Id, "fp-c", "pc-c");

        var devices = _store.ListDevices(owner.Id);

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, device => device.Id == first.Id);
        Assert.Contains(devices, device => device.Id == second.Id);
        Assert.DoesNotContain(devices, device => device.UserId != owner.Id);
    }

    [Fact]
    public void DeleteUser_RemovesAccountAndPersonalRows()
    {
        var user = CreateUser("delete-account@example.com");
        var device = _store.RegisterDevice(user.Id, "fp-delete", "pc").Device!;
        _store.RecordUsage(user.Id, device.Id, 120, "qwen-mt-flash", 1, 1, 500, _database.NowUnix);
        _store.SaveRefreshToken(user.Id, "delete-token-hash", _database.NowUnix + 600);
        _store.SaveVerificationCode(user.Email, "hash", _database.NowUnix + 300, _database.NowUnix,
            VerificationCodeService.ResetPurpose);
        var notes = new NoteStore(_database);
        notes.Create(user.Id, NoteKind.Word, "hello", "你好", "en", "zh", "qwen-mt-flash", "test");

        Assert.True(_store.DeleteUser(user.Id, user.Email));

        // 用户行删掉后，设备、笔记、refresh token 都靠外键级联；usage_events 与验证码
        // 没有指向 users 的外键，DeleteUser 必须显式清掉，否则会留下可关联的个人数据。
        Assert.Null(_store.FindUserById(user.Id));
        Assert.Null(_store.ConsumeRefreshToken("delete-token-hash"));
        Assert.Null(_store.FindVerificationCode(user.Email));
        Assert.Equal(0, notes.CountLive(user.Id));
        Assert.Equal(0L, _store.ReadTodayCostMicros());
    }

    [Fact]
    public void DeleteDevice_RemovesTheRowAndItsDailyUsage()
    {
        var user = CreateUser("delete-device@example.com");
        var removed = _store.RegisterDevice(user.Id, "fp-remove", "pc").Device!;
        var kept = _store.RegisterDevice(user.Id, "fp-keep", "pc").Device!;
        _store.RecordUsage(user.Id, removed.Id, 120, "qwen-mt-flash", 0, 0, 0, _database.NowUnix);

        Assert.True(_store.DeleteDevice(user.Id, removed.Id));

        Assert.Null(_store.FindDevice(user.Id, removed.Id));
        Assert.NotNull(_store.FindDevice(user.Id, kept.Id));
        // 子行必须显式删除：SQLite 的 foreign_keys 是连接级开关，CASCADE 不会生效
        Assert.Equal(0, _store.ReadUsage(user.Id, removed.Id).DailyChars);
    }

    [Fact]
    public void DeleteDevice_RefusesAnotherAccountsDevice()
    {
        var owner = CreateUser("owner-device@example.com");
        var intruder = CreateUser("intruder-device@example.com");
        var device = _store.RegisterDevice(owner.Id, "fp-owned", "pc").Device!;
        _store.RecordUsage(owner.Id, device.Id, 50, "qwen-mt-flash", 0, 0, 0, _database.NowUnix);

        Assert.False(_store.DeleteDevice(intruder.Id, device.Id));

        Assert.NotNull(_store.FindDevice(owner.Id, device.Id));
        Assert.Equal(50, _store.ReadUsage(owner.Id, device.Id).DailyChars);
    }

    [Fact]
    public void DeleteDevice_RevokesOnlyThatDevicesRefreshTokens()
    {
        var user = CreateUser("revoke-device@example.com");
        var removed = _store.RegisterDevice(user.Id, "fp-revoke", "pc").Device!;
        var kept = _store.RegisterDevice(user.Id, "fp-keep-revoke", "pc").Device!;
        var removedHash = FastTranslation.Server.Auth.TokenService.HashRefreshToken("removed-chain");
        var keptHash = FastTranslation.Server.Auth.TokenService.HashRefreshToken("kept-chain");
        _store.SaveRefreshToken(user.Id, removedHash, _database.NowUnix + 600, removed.Id);
        _store.SaveRefreshToken(user.Id, keptHash, _database.NowUnix + 600, kept.Id);

        Assert.True(_store.DeleteDevice(user.Id, removed.Id));

        Assert.Null(_store.ConsumeRefreshToken(removedHash));
        var survivor = _store.ConsumeRefreshToken(keptHash);
        Assert.Equal(user.Id, survivor!.UserId);
        Assert.Equal(kept.Id, survivor.DeviceId);
    }

    [Fact]
    public void Migration_OldDatabaseGainsTheRefreshDeviceColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=false"))
            {
                raw.Open();
                using var create = raw.CreateCommand();
                create.CommandText = """
                    CREATE TABLE users (
                        id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL,
                        plan INTEGER NOT NULL DEFAULT 0, plan_expires_at INTEGER,
                        created_at INTEGER NOT NULL, disabled INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE refresh_tokens (
                        id TEXT PRIMARY KEY, user_id TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
                        created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, revoked_at INTEGER);
                    """;
                create.ExecuteNonQuery();
            }

            var database = new ServerDatabase(path);
            database.Initialize();

            using var connection = database.Open();
            using var probe = connection.CreateCommand();
            probe.CommandText = "PRAGMA table_info(refresh_tokens)";
            using var reader = probe.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(1));

            Assert.Contains("device_id", columns);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(path);
                File.Delete(path + "-wal");
                File.Delete(path + "-shm");
            }
            catch (IOException)
            {
                // 尽力而为:残留的临时文件绝不能导致测试运行失败。
            }
        }
    }

    [Fact]
    public void RecordUsage_AccumulatesMonthlyAndDaily()
    {
        var user = CreateUser("usage@example.com");
        var device = _store.RegisterDevice(user.Id, "fp-u", "pc").Device!;

        _store.RecordUsage(user.Id, device.Id, 100, "qwen-mt-flash", 10, 5, 0, _database.NowUnix);
        _store.RecordUsage(user.Id, device.Id, 250, "qwen-mt-flash", 20, 8, 0, _database.NowUnix);

        var usage = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal(350, usage.MonthlyChars);
        Assert.Equal(350, usage.DailyChars);
        Assert.Equal(_database.ThisMonthKey, usage.Month);
        Assert.Equal(_database.TodayKey, usage.Day);
    }

    [Fact]
    public void ReadUsage_ReturnsZeroForUnknownAccount()
        => Assert.Equal(0, _store.ReadUsage(Guid.NewGuid(), null).MonthlyChars);

    [Fact]
    public void RefreshToken_IsOneShot()
    {
        var user = CreateUser("refresh@example.com");
        var token = "refresh-abc";
        var hash = FastTranslation.Server.Auth.TokenService.HashRefreshToken(token);
        _store.SaveRefreshToken(user.Id, hash, _database.NowUnix + 600);

        Assert.Equal(user.Id, _store.ConsumeRefreshToken(hash)!.UserId);
        Assert.Null(_store.ConsumeRefreshToken(hash));
    }

    [Fact]
    public void RefreshToken_ExpiredIsRejected()
    {
        var user = CreateUser("expired@example.com");
        var hash = FastTranslation.Server.Auth.TokenService.HashRefreshToken("t");
        _store.SaveRefreshToken(user.Id, hash, _database.NowUnix - 1);

        Assert.Null(_store.ConsumeRefreshToken(hash));
    }

    [Fact]
    public void RevokeAllRefreshTokens_InvalidatesEveryChain()
    {
        var user = CreateUser("revoke@example.com");
        var a = FastTranslation.Server.Auth.TokenService.HashRefreshToken("a");
        var b = FastTranslation.Server.Auth.TokenService.HashRefreshToken("b");
        _store.SaveRefreshToken(user.Id, a, _database.NowUnix + 600);
        _store.SaveRefreshToken(user.Id, b, _database.NowUnix + 600);

        Assert.Equal(2, _store.RevokeAllRefreshTokens(user.Id));
        Assert.Null(_store.ConsumeRefreshToken(a));
        Assert.Null(_store.ConsumeRefreshToken(b));
    }

    [Fact]
    public void VerificationCode_RoundTripsAndCanBeDeleted()
    {
        var hash = "abc123";
        _store.SaveVerificationCode("Code@Example.com", hash, _database.NowUnix + 300, _database.NowUnix,
            VerificationCodeService.RegisterPurpose);

        var record = _store.FindVerificationCode("code@example.com");
        Assert.NotNull(record);
        Assert.Equal(hash, record!.CodeHash);
        Assert.Equal(0, record.Attempts);
        Assert.Equal(VerificationCodeService.RegisterPurpose, record.Purpose);

        _store.IncrementVerificationAttempts("code@example.com");
        Assert.Equal(1, _store.FindVerificationCode("code@example.com")!.Attempts);

        _store.DeleteVerificationCode("code@example.com");
        Assert.Null(_store.FindVerificationCode("code@example.com"));
    }

    [Fact]
    public void Metrics_CountsPerPlanAndActiveUsers()
    {
        var free = CreateUser("f@example.com");
        var light = _store.CreateUser("l@example.com", "hash", PlanTier.Light);
        _store.CreateUser("s@example.com", "hash", PlanTier.Standard);
        var device = _store.RegisterDevice(free.Id, "fp", "pc").Device!;
        _store.RecordUsage(free.Id, device.Id, 10, "m", 0, 0, 0, _database.NowUnix);

        var metrics = _store.ReadAccountMetrics();

        Assert.Equal(3, metrics.Total);
        Assert.Equal(1, metrics.Free);
        Assert.Equal(1, metrics.Light);
        Assert.Equal(1, metrics.Standard);
        Assert.Equal(1, metrics.ActiveLast30Days);
    }
}
