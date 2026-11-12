using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>用可拨动的时钟验证个人额度周期：开通、续费、失效重开三种状态。</summary>
public sealed class QuotaPeriodStoreTests : IDisposable
{
    private readonly string _databasePath;
    private readonly ManualTimeProvider _clock;
    private readonly AccountStore _store;

    public QuotaPeriodStoreTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), "ft-server-tests", Guid.NewGuid().ToString("N") + ".db");
        _clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        var database = new ServerDatabase(_databasePath, _clock);
        database.Initialize();
        _store = new AccountStore(database);
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

    private (UserRecord User, DeviceRecord Device) NewUser()
    {
        var user = _store.CreateUser("cycle@example.com", "hash", PlanTier.Free);
        return (user, _store.RegisterDevice(user.Id, "fp", "pc").Device!);
    }

    private void Record(UserRecord user, DeviceRecord device, int chars)
        => _store.RecordUsage(user.Id, device.Id, chars, "qwen-mt-flash", 10, 5, 0, _clock.Unix);

    [Fact]
    public void Usage_rolls_on_the_activation_anniversary()
    {
        var (user, device) = NewUser();
        Record(user, device, 300);
        var usage = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal(300, usage.MonthlyChars);
        Assert.Equal("2026-11-12", usage.PeriodStart);

        _clock.Advance(TimeSpan.FromDays(30));
        usage = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal(0, usage.MonthlyChars);
        Assert.Equal("2026-12-12", usage.PeriodStart);
    }

    [Fact]
    public void Activation_from_free_starts_a_new_period()
    {
        var (user, device) = NewUser();
        Record(user, device, 500);
        _clock.Advance(TimeSpan.FromDays(20));
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);

        var usage = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal(0, usage.MonthlyChars);
        Assert.Equal("2026-12-02", usage.PeriodStart);
    }

    [Fact]
    public void Renewal_while_active_keeps_the_anchor_and_usage()
    {
        var (user, device) = NewUser();
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);
        _clock.Advance(TimeSpan.FromDays(10));
        Record(user, device, 200);

        var before = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal("2026-11-12", before.PeriodStart);
        Assert.Equal(200, before.MonthlyChars);

        _clock.Advance(TimeSpan.FromDays(15));
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);

        var after = _store.ReadUsage(user.Id, device.Id);
        Assert.Equal("2026-11-12", after.PeriodStart);
        Assert.Equal(200, after.MonthlyChars);
    }

    [Fact]
    public void Reactivation_after_expiry_starts_a_new_period()
    {
        var (user, device) = NewUser();
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);
        _clock.Advance(TimeSpan.FromDays(40));
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);

        Assert.Equal("2026-12-22", _store.ReadUsage(user.Id, device.Id).PeriodStart);
    }

    [Fact]
    public void Admin_grant_from_free_starts_a_new_period()
    {
        var (user, device) = NewUser();
        _clock.Advance(TimeSpan.FromDays(20));
        _store.SetPlan(user.Id, PlanTier.Light, _clock.Unix + 30 * 86_400L);

        Assert.Equal("2026-12-02", _store.ReadUsage(user.Id, device.Id).PeriodStart);
    }

    [Fact]
    public void Admin_upgrade_while_active_keeps_the_anchor()
    {
        var (user, device) = NewUser();
        _store.ExtendPlan(user.Id, PlanTier.Light, 30, _clock.Unix);
        _clock.Advance(TimeSpan.FromDays(10));
        _store.SetPlan(user.Id, PlanTier.Standard, _clock.Unix + 30 * 86_400L);

        Assert.Equal("2026-11-12", _store.ReadUsage(user.Id, device.Id).PeriodStart);
    }
}
