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
    }

    /// <summary>
    /// All timestamps are unix seconds (UTC). <c>users.plan</c> mirrors <see cref="Domain.PlanTier"/>.
    /// <c>usage_monthly</c> is per account, <c>usage_daily</c> is per device — the free tier's
    /// daily cap is per device by design, so that a shared account cannot pool it (design §7.4).
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
        """;
}
