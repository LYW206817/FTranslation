using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>账本比写它的端点活得久,所以存储规则不经过 HTTP 来测:
/// 已作废的码绝不能可兑现,而现有生产库必须补上该列。</summary>
public class RedeemStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ft-server-tests", "redeem-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void RevokedCodeCannotBeClaimed()
    {
        var (store, database) = Open();
        var code = RedeemCode.Generate();
        store.CreateCodes([code], PlanTier.Light, 30, "batch", database.NowUnix);

        Assert.True(store.Revoke(code, database.NowUnix));
        Assert.False(store.TryClaim(code, SeedUser(database), database.NowUnix));

        var record = store.Find(code);
        Assert.NotNull(record);
        Assert.True(record!.IsRevoked);
        Assert.False(record.IsRedeemed);
    }

    [Fact]
    public void RevokeLeavesSpentCodesAlone()
    {
        var (store, database) = Open();
        var code = RedeemCode.Generate();
        store.CreateCodes([code], PlanTier.Standard, 90, "batch", database.NowUnix);
        var buyer = SeedUser(database);
        Assert.True(store.TryClaim(code, buyer, database.NowUnix));

        // 买家已经付过钱:把码收回会删掉他们拥有的天数。
        Assert.False(store.Revoke(code, database.NowUnix));
        Assert.Null(store.Find(code)!.RevokedAt);
    }

    [Fact]
    public void LedgerSeparatesFates()
    {
        var (store, database) = Open();
        var codes = new[] { RedeemCode.Generate(), RedeemCode.Generate(), RedeemCode.Generate() };
        store.CreateCodes(codes, PlanTier.Light, 30, "batch", database.NowUnix);
        store.TryClaim(codes[0], SeedUser(database), database.NowUnix);
        store.Revoke(codes[2], database.NowUnix);

        Assert.Equal(1, store.Count("open"));
        Assert.Equal(1, store.Count("used"));
        Assert.Equal(1, store.Count("revoked"));
        Assert.Equal(3, store.Count("all"));
        Assert.Equal(codes[1], Assert.Single(store.List("open", 50, 0)).Code);
        Assert.False(RedeemStore.IsKnownStatus("pending"));
    }

    /// <summary>生产环境已经有 redeem_codes 表,但没有 revoked_at,而
    /// CREATE TABLE IF NOT EXISTS 从不加列,所以这里故意构造 v49 之前的形态并迁移它。</summary>
    [Fact]
    public void Migration_OldDatabaseGainsTheRevokedColumn()
    {
        const string oldSchema = """
            CREATE TABLE users (
                id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL,
                plan INTEGER NOT NULL DEFAULT 0, plan_expires_at INTEGER,
                created_at INTEGER NOT NULL, disabled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE redeem_codes (
                code TEXT PRIMARY KEY, plan INTEGER NOT NULL, days INTEGER NOT NULL,
                note TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL,
                redeemed_by TEXT, redeemed_at INTEGER);
            INSERT INTO redeem_codes (code, plan, days, note, created_at)
                VALUES ('FT-4G7M-2XQP-9HKV-3WTA', 1, 30, 'printed before revocation existed', 1700000000);
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var raw = new SqliteConnection($"Data Source={_path};Pooling=false"))
        {
            raw.Open();
            using var create = raw.CreateCommand();
            create.CommandText = oldSchema;
            create.ExecuteNonQuery();
        }

        var database = new ServerDatabase(_path);
        database.Initialize();

        using var connection = database.Open();
        using var probe = connection.CreateCommand();
        probe.CommandText = "PRAGMA table_info(redeem_codes)";
        using var reader = probe.ExecuteReader();
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns[reader.GetString(1)] = reader.GetString(2);
        }

        Assert.Equal("INTEGER", columns["revoked_at"]);

        // 迁移之前印出的码仍然可售,也仍然可作废。
        var store = new RedeemStore(database);
        var legacy = store.Find("FT-4G7M-2XQP-9HKV-3WTA");
        Assert.NotNull(legacy);
        Assert.False(legacy!.IsRevoked);
        Assert.Equal(PlanTier.Light, legacy.Plan);
        Assert.True(store.Revoke("FT-4G7M-2XQP-9HKV-3WTA", database.NowUnix));
        Assert.True(store.Find("FT-4G7M-2XQP-9HKV-3WTA")!.IsRevoked);
    }

    /// <summary>redeemed_by 是真正的外键,所以兑现需要一个存在的买家。</summary>
    private static Guid SeedUser(ServerDatabase database)
    {
        var id = Guid.NewGuid();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (id, email, password_hash, plan, created_at, disabled)
            VALUES ($id, $email, 'hash', 0, 1700000000, 0)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$email", "buyer-" + id.ToString("N") + "@example.com");
        command.ExecuteNonQuery();
        return id;
    }

    private (RedeemStore Store, ServerDatabase Database) Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var database = new ServerDatabase(_path);
        database.Initialize();
        return (new RedeemStore(database), database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); }
            catch (IOException) { }
        }
    }
}
