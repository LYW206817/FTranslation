using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>Exercises the real SQLite schema against a throwaway file — no in-memory fakes, because
/// the quota upserts and the device cap are exactly the parts a fake would hide.</summary>
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
            // Best effort: a stray temp file must never fail a test run.
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

        Assert.Equal(user.Id, _store.ConsumeRefreshToken(hash));
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
        _store.SaveVerificationCode("Code@Example.com", hash, _database.NowUnix + 300, _database.NowUnix);

        var record = _store.FindVerificationCode("code@example.com");
        Assert.NotNull(record);
        Assert.Equal(hash, record!.CodeHash);
        Assert.Equal(0, record.Attempts);

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
