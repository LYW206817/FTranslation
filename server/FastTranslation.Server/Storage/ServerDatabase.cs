using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

/// <summary>
/// Owns the SQLite file and its schema. One short-lived connection per operation: SQLite serializes
/// writers anyway, and a pooled connection avoids the "database is locked" dance under concurrency.
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

        // CREATE TABLE IF NOT EXISTS never adds a column to an existing table, so every column we
        // introduce after v43 needs an explicit, idempotent ALTER here. Skipping this silently
        // leaves production databases on the old shape and the reads below start throwing.
        EnsureColumn(connection, "users", "plan_expires_at", "INTEGER");
        EnsureColumn(connection, "usage_events", "cost_micros", "INTEGER NOT NULL DEFAULT 0");

        // Review state (design §4.1). All six columns are NOT NULL DEFAULT so an old database
        // migrates in place: every existing note becomes an ungraded card at stage 0.
        EnsureColumn(connection, "notes", "stage", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "due_at", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "last_grade", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notes", "review_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "lapses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "notes", "reviewed_at", "INTEGER NOT NULL DEFAULT 0");

        // The review queue is the only query that scans by due time; the partial index keeps it
        // small. It is created here rather than in Schema because on an existing database the
        // notes table only gains due_at in the EnsureColumn calls above.
        using var index = connection.CreateCommand();
        index.CommandText =
            "CREATE INDEX IF NOT EXISTS ix_notes_user_due ON notes(user_id, due_at) WHERE deleted_at IS NULL";
        index.ExecuteNonQuery();
    }

    /// <summary>Adds a column only when the table exists and does not already carry it.</summary>
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
    /// All timestamps are unix seconds (UTC). <c>users.plan</c> mirrors <see cref="Domain.PlanTier"/>.
    /// <c>users.plan_expires_at</c> NULL means "granted without an end date" (see Domain/Entitlement).
    /// <c>usage_monthly</c> is per account, <c>usage_daily</c> is per device — the free tier's
    /// daily cap is per device by design, so that a shared account cannot pool it (design §7.4).
    /// <c>usage_events.cost_micros</c> is micro-yuan so the whole spend ledger stays integer.
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
            disabled      INTEGER NOT NULL DEFAULT 0
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
            reviewed_at INTEGER NOT NULL DEFAULT 0
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
            redeemed_at INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_redeem_codes_redeemed ON redeem_codes(redeemed_at);
        """;
}
