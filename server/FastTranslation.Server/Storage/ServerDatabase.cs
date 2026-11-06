using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

/// <summary>
/// 掌管 SQLite 文件及其 schema。每次操作使用短生命周期的连接：SQLite 本身会串行化
/// 写入者，而连接池可以避免并发下反复出现“database is locked”。
/// </summary>
public sealed class ServerDatabase
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public ServerDatabase(string databasePath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _time = time ?? TimeProvider.System;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public long NowUnix => _time.GetUtcNow().ToUnixTimeSeconds();

    public string TodayKey => _time.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public string ThisMonthKey => _time.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();

        // CREATE TABLE IF NOT EXISTS 永远不会给已有表加列，所以 v43 之后新增的每一列
        // 都需要在这里显式、幂等地 ALTER 一次。漏掉这一步会悄悄
        // 让生产库停留在旧结构上，随后下面的读取就会开始抛异常。
        EnsureColumn(connection, "users", "plan_expires_at", "INTEGER");
        EnsureColumn(connection, "usage_events", "cost_micros", "INTEGER NOT NULL DEFAULT 0");

        // 显示名是用户唯一可编辑的资料字段（账号页）。为空表示“使用邮箱
        // 本地部分”，这样旧行与新行行为一致，无需回填。
        EnsureColumn(connection, "users", "display_name", "TEXT NOT NULL DEFAULT ''");

        // 从未售出前就录错或泄露的码必须能被作废，否则唯一出路就是
        // 删除该行，同时丢失审计记录。
        EnsureColumn(connection, "redeem_codes", "revoked_at", "INTEGER");

        // 复习状态（设计 §4.1）。六列全是 NOT NULL DEFAULT，这样旧数据库可以
        // 原地迁移：每条已有记录都成为第 0 档的未评分卡片。
        EnsureColumn(connection, "notes", "stage", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "due_at", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "last_grade", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notes", "review_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "lapses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "reviewed_at", "INTEGER NOT NULL DEFAULT 0");

        // v101: 新卡日配额要按「今天第一次评分」计数，而不是「今天创建」。
        // 只用 stage/reviewed_at 区分不了「今天首学」与「老卡今天复习」，所以单独记第一次评分时间。
        EnsureColumn(connection, "notes", "first_reviewed_at", "INTEGER NOT NULL DEFAULT 0");

        // 迁移前就已经评过分的卡：用最后一次评分时间回填，避免它们在下一次复习时
        // 被误算成「今天首次学习」，白吃掉当天的 20 张新卡配额。
        using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE notes SET first_reviewed_at = reviewed_at
                WHERE stage >= 1 AND first_reviewed_at = 0
                """;
            backfill.ExecuteNonQuery();
        }

        // 复习队列是按到期时间扫描的唯一查询；这个部分索引让它保持
        // 很小。之所以在这里建而不是放进 Schema，是因为在已有数据库上，
        // notes 表要到上面的 EnsureColumn 调用之后才拥有 due_at。
        using var index = connection.CreateCommand();
        index.CommandText =
            "CREATE INDEX IF NOT EXISTS ix_notes_user_due ON notes(user_id, due_at) WHERE deleted_at IS NULL";
        index.ExecuteNonQuery();

        // 日配额只查「今天首次评分的卡」，索引把扫描限制在这个窗口内。
        using var firstReviewedIndex = connection.CreateCommand();
        firstReviewedIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_notes_user_first_reviewed
            ON notes(user_id, first_reviewed_at) WHERE deleted_at IS NULL AND stage >= 1
            """;
        firstReviewedIndex.ExecuteNonQuery();
    }

    /// <summary>仅当表存在且尚未包含该列时才添加。</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"PRAGMA table_info({table})";
        using var reader = probe.ExecuteReader();
        var present = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                present = true;
                break;
            }
        }

        reader.Close();
        if (present)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 所有时间戳都是 Unix 秒（UTC）。<c>users.plan</c> 对应 <see cref="Domain.PlanTier"/>。
    /// <c>users.plan_expires_at</c> 为 NULL 表示“授权没有到期时间”（见 Domain/Entitlement）。
    /// <c>usage_monthly</c> 按账号计，<c>usage_daily</c> 按设备计——免费档的
    /// 每日上限按设计是按设备计的，这样共享账号也无法把它汇总使用（设计 §7.4）。
    /// <c>usage_events.cost_micros</c> 以微元为单位，这样整个支出账本保持整数。
    /// </summary>
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 5000;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS users (
            id            TEXT PRIMARY KEY,
            email         TEXT NOT NULL UNIQUE,
            password_hash TEXT NOT NULL,
            plan          INTEGER NOT NULL DEFAULT 0,
            plan_expires_at INTEGER,
            created_at    INTEGER NOT NULL,
            disabled      INTEGER NOT NULL DEFAULT 0,
            display_name  TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS devices (
            id            TEXT PRIMARY KEY,
            user_id       TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            fingerprint   TEXT NOT NULL,
            name          TEXT NOT NULL DEFAULT '',
            created_at    INTEGER NOT NULL,
            last_seen_at  INTEGER NOT NULL,
            UNIQUE (user_id, fingerprint)
        );

        CREATE INDEX IF NOT EXISTS ix_devices_user ON devices(user_id);

        CREATE TABLE IF NOT EXISTS usage_monthly (
            user_id  TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            month    TEXT NOT NULL,
            chars    INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (user_id, month)
        );

        CREATE TABLE IF NOT EXISTS usage_daily (
            device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
            day       TEXT NOT NULL,
            chars     INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (device_id, day)
        );

        CREATE TABLE IF NOT EXISTS usage_events (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id    TEXT NOT NULL,
            device_id  TEXT NOT NULL,
            model      TEXT NOT NULL DEFAULT '',
            chars      INTEGER NOT NULL,
            tokens_in  INTEGER NOT NULL DEFAULT 0,
            tokens_out INTEGER NOT NULL DEFAULT 0,
            cost_micros INTEGER NOT NULL DEFAULT 0,
            source     TEXT NOT NULL DEFAULT 'gateway',
            created_at INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_usage_events_user_time ON usage_events(user_id, created_at);

        CREATE TABLE IF NOT EXISTS refresh_tokens (
            id         TEXT PRIMARY KEY,
            user_id    TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            token_hash TEXT NOT NULL UNIQUE,
            created_at INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            revoked_at INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_refresh_tokens_user ON refresh_tokens(user_id);

        CREATE TABLE IF NOT EXISTS email_verifications (
            email        TEXT PRIMARY KEY,
            code_hash    TEXT NOT NULL,
            expires_at   INTEGER NOT NULL,
            attempts     INTEGER NOT NULL DEFAULT 0,
            last_sent_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notes (
            id          TEXT PRIMARY KEY,
            user_id     TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            kind        TEXT NOT NULL,
            source_text TEXT NOT NULL,
            target_text TEXT NOT NULL,
            source_lang TEXT NOT NULL DEFAULT 'auto',
            target_lang TEXT NOT NULL DEFAULT 'auto',
            model       TEXT NOT NULL DEFAULT '',
            origin_app  TEXT NOT NULL DEFAULT '',
            note        TEXT NOT NULL DEFAULT '',
            created_at  INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL,
            deleted_at  INTEGER,
            stage       INTEGER NOT NULL DEFAULT 0,
            due_at      INTEGER NOT NULL DEFAULT 0,
            last_grade  TEXT NOT NULL DEFAULT '',
            review_count INTEGER NOT NULL DEFAULT 0,
            lapses      INTEGER NOT NULL DEFAULT 0,
            reviewed_at INTEGER NOT NULL DEFAULT 0,
            first_reviewed_at INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_notes_user_kind ON notes(user_id, kind, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_notes_user_live ON notes(user_id, deleted_at);

        CREATE TABLE IF NOT EXISTS redeem_codes (
            code        TEXT PRIMARY KEY,
            plan        INTEGER NOT NULL,
            days        INTEGER NOT NULL,
            note        TEXT NOT NULL DEFAULT '',
            created_at  INTEGER NOT NULL,
            redeemed_by TEXT REFERENCES users(id) ON DELETE SET NULL,
            redeemed_at INTEGER,
            revoked_at  INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_redeem_codes_redeemed ON redeem_codes(redeemed_at);
        """;
}
