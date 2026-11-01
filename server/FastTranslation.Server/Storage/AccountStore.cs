using System.Globalization;
using FastTranslation.Server.Domain;
using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

/// <summary>
/// <paramref name="PlanExpiresAt"/> 为 null 表示授权没有到期时间。至于为什么这表示
/// “永久有效”而不是“已过期”，见 Domain/Entitlement。
/// </summary>
public sealed record UserRecord(Guid Id, string Email, string PasswordHash, PlanTier Plan, long? PlanExpiresAt, long CreatedAt, bool Disabled, string DisplayName = "");

public sealed record DeviceRecord(Guid Id, Guid UserId, string Fingerprint, string Name, long CreatedAt, long LastSeenAt);

public sealed record UsageSnapshot(int MonthlyChars, int DailyChars, string Month, string Day);

/// <summary>
/// 服务里所有 SQL 语句都在这里。处理器保持薄，额度计算留在
/// <see cref="QuotaPolicy"/>——本类型只负责与 SQLite 之间搬字节。
/// </summary>
public sealed class AccountStore(ServerDatabase database)
{
    public UserRecord? FindUserByEmail(string email)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email, password_hash, plan, plan_expires_at, created_at, disabled, display_name FROM users WHERE email = $email";
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public UserRecord? FindUserById(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email, password_hash, plan, plan_expires_at, created_at, disabled, display_name FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public UserRecord CreateUser(string email, string passwordHash, PlanTier plan, long? planExpiresAt = null)
    {
        var record = new UserRecord(Guid.NewGuid(), NormalizeEmail(email), passwordHash, plan, planExpiresAt, database.NowUnix, false);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (id, email, password_hash, plan, plan_expires_at, created_at, disabled)
            VALUES ($id, $email, $hash, $plan, $planExpiresAt, $createdAt, 0)
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$email", record.Email);
        command.Parameters.AddWithValue("$hash", record.PasswordHash);
        command.Parameters.AddWithValue("$plan", (int)record.Plan);
        command.Parameters.AddWithValue("$planExpiresAt", (object?)record.PlanExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt);
        command.ExecuteNonQuery();
        return record;
    }

    /// <summary>
    /// 授予套餐。<paramref name="expiresAtUnix"/> 为 null 会清除到期时间，这正是
    /// 人工激活码想要的效果：账号一直可用，直到运维另行处置。
    /// </summary>
    public bool SetPlan(Guid userId, PlanTier plan, long? expiresAtUnix = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET plan = $plan, plan_expires_at = $expiresAt WHERE id = $id";
        command.Parameters.AddWithValue("$plan", (int)plan);
        command.Parameters.AddWithValue("$expiresAt", (object?)expiresAtUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 显示名是可选的装饰：空字符串表示“改为显示邮箱”，所以
    /// 清空它是合法更新，而不是校验失败。
    /// </summary>
    public bool SetDisplayName(Guid userId, string displayName)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET display_name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetPasswordHash(Guid userId, string passwordHash)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id";
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>今天所有账号的总支出，单位为微元——网关的预算熔断依据。</summary>
    public long ReadTodayCostMicros()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(cost_micros), 0) FROM usage_events WHERE created_at >= $since";
        command.Parameters.AddWithValue("$since", database.NowUnix - (database.NowUnix % 86_400));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>今天某个来源已花掉的成本(微元)。语音有独立日预算,要按 source 单独算一个桶。</summary>
    public long ReadTodayCostMicros(string source)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(cost_micros), 0) FROM usage_events WHERE created_at >= $since AND source = $source";
        command.Parameters.AddWithValue("$since", database.NowUnix - (database.NowUnix % 86_400));
        command.Parameters.AddWithValue("$source", source);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>返回该指纹对应的设备；账号未达设备上限时会创建。</summary>
    public DeviceResult RegisterDevice(Guid userId, string fingerprint, string name)
    {
        var normalized = fingerprint.Trim();

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id, user_id, fingerprint, name, created_at, last_seen_at FROM devices WHERE user_id = $userId AND fingerprint = $fp";
            find.Parameters.AddWithValue("$userId", userId.ToString("D"));
            find.Parameters.AddWithValue("$fp", normalized);

            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadDevice(reader);
                reader.Close();

                var updated = existing with { Name = Shorten(name, 64), LastSeenAt = database.NowUnix };

                using var touch = connection.CreateCommand();
                touch.Transaction = transaction;
                touch.CommandText = "UPDATE devices SET last_seen_at = $now, name = $name WHERE id = $id";
                touch.Parameters.AddWithValue("$now", updated.LastSeenAt);
                touch.Parameters.AddWithValue("$name", updated.Name);
                touch.Parameters.AddWithValue("$id", existing.Id.ToString("D"));
                touch.ExecuteNonQuery();

                transaction.Commit();
                return new DeviceResult(true, updated, null);
            }
        }

        int count;
        using (var count_ = connection.CreateCommand())
        {
            count_.Transaction = transaction;
            count_.CommandText = "SELECT COUNT(*) FROM devices WHERE user_id = $userId";
            count_.Parameters.AddWithValue("$userId", userId.ToString("D"));
            count = Convert.ToInt32(count_.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (count >= Plans.MaxDevicesPerAccount)
        {
            transaction.Rollback();
            return new DeviceResult(false, null, "device_limit_reached");
        }

        var device = new DeviceRecord(Guid.NewGuid(), userId, normalized, Shorten(name, 64), database.NowUnix, database.NowUnix);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO devices (id, user_id, fingerprint, name, created_at, last_seen_at)
                VALUES ($id, $userId, $fp, $name, $now, $now)
                """;
            insert.Parameters.AddWithValue("$id", device.Id.ToString("D"));
            insert.Parameters.AddWithValue("$userId", userId.ToString("D"));
            insert.Parameters.AddWithValue("$fp", device.Fingerprint);
            insert.Parameters.AddWithValue("$name", device.Name);
            insert.Parameters.AddWithValue("$now", database.NowUnix);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new DeviceResult(true, device, null);
    }

    /// <summary>网关热路径上用的只读设备查询（避免每次请求都写库）。</summary>
    public DeviceRecord? FindDeviceByFingerprint(Guid userId, string fingerprint)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, fingerprint, name, created_at, last_seen_at
            FROM devices WHERE user_id = $userId AND fingerprint = $fingerprint
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", fingerprint.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    public DeviceRecord? FindDevice(Guid userId, Guid deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, user_id, fingerprint, name, created_at, last_seen_at FROM devices WHERE id = $id AND user_id = $userId";
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    public UsageSnapshot ReadUsage(Guid userId, Guid? deviceId)
    {
        using var connection = database.Open();

        int monthly = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT chars FROM usage_monthly WHERE user_id = $userId AND month = $month";
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
            command.Parameters.AddWithValue("$month", database.ThisMonthKey);
            monthly = command.ExecuteScalar() is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }

        int daily = 0;
        if (deviceId is { } id)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT chars FROM usage_daily WHERE device_id = $deviceId AND day = $day";
            command.Parameters.AddWithValue("$deviceId", id.ToString("D"));
            command.Parameters.AddWithValue("$day", database.TodayKey);
            daily = command.ExecuteScalar() is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }

        return new UsageSnapshot(monthly, daily, database.ThisMonthKey, database.TodayKey);
    }

    public void RecordUsage(Guid userId, Guid deviceId, int chars, string model, int tokensIn, int tokensOut, long costMicros, long now, string source = "gateway")
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var monthly = connection.CreateCommand())
        {
            monthly.Transaction = transaction;
            monthly.CommandText = """
                INSERT INTO usage_monthly (user_id, month, chars) VALUES ($userId, $month, $chars)
                ON CONFLICT(user_id, month) DO UPDATE SET chars = chars + excluded.chars
                """;
            monthly.Parameters.AddWithValue("$userId", userId.ToString("D"));
            monthly.Parameters.AddWithValue("$month", database.ThisMonthKey);
            monthly.Parameters.AddWithValue("$chars", chars);
            monthly.ExecuteNonQuery();
        }

        using (var daily = connection.CreateCommand())
        {
            daily.Transaction = transaction;
            daily.CommandText = """
                INSERT INTO usage_daily (device_id, day, chars) VALUES ($deviceId, $day, $chars)
                ON CONFLICT(device_id, day) DO UPDATE SET chars = chars + excluded.chars
                """;
            daily.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            daily.Parameters.AddWithValue("$day", database.TodayKey);
            daily.Parameters.AddWithValue("$chars", chars);
            daily.ExecuteNonQuery();
        }

        using (var entry = connection.CreateCommand())
        {
            entry.Transaction = transaction;
            entry.CommandText = """
                INSERT INTO usage_events (user_id, device_id, model, chars, tokens_in, tokens_out, cost_micros, source, created_at)
                VALUES ($userId, $deviceId, $model, $chars, $tokensIn, $tokensOut, $cost, $source, $now)
                """;
            entry.Parameters.AddWithValue("$userId", userId.ToString("D"));
            entry.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            entry.Parameters.AddWithValue("$model", model);
            entry.Parameters.AddWithValue("$chars", chars);
            entry.Parameters.AddWithValue("$tokensIn", tokensIn);
            entry.Parameters.AddWithValue("$tokensOut", tokensOut);
            entry.Parameters.AddWithValue("$cost", costMicros);
        entry.Parameters.AddWithValue("$source", source);
            entry.Parameters.AddWithValue("$now", now);
            entry.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SaveRefreshToken(Guid userId, string tokenHash, long expiresAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO refresh_tokens (id, user_id, token_hash, created_at, expires_at, revoked_at)
            VALUES ($id, $userId, $hash, $now, $expiresAt, NULL)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", database.NowUnix);
        command.Parameters.AddWithValue("$expiresAt", expiresAt);
        command.ExecuteNonQuery();
    }

    /// <summary>消费一个 refresh token（一次性轮换）。返回所属用户 id，无效时返回 null。</summary>
    public Guid? ConsumeRefreshToken(string tokenHash)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        Guid userId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT user_id FROM refresh_tokens
                WHERE token_hash = $hash AND revoked_at IS NULL AND expires_at > $now
                """;
            select.Parameters.AddWithValue("$hash", tokenHash);
            select.Parameters.AddWithValue("$now", database.NowUnix);

            if (select.ExecuteScalar() is not string text || !Guid.TryParse(text, out userId))
            {
                transaction.Rollback();
                return null;
            }
        }

        using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE refresh_tokens SET revoked_at = $now WHERE token_hash = $hash";
            revoke.Parameters.AddWithValue("$hash", tokenHash);
            revoke.Parameters.AddWithValue("$now", database.NowUnix);
            revoke.ExecuteNonQuery();
        }

        transaction.Commit();
        return userId;
    }

    public int RevokeAllRefreshTokens(Guid userId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE refresh_tokens SET revoked_at = $now WHERE user_id = $userId AND revoked_at IS NULL";
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$now", database.NowUnix);
        return command.ExecuteNonQuery();
    }

    public void SaveVerificationCode(string email, string codeHash, long expiresAt, long sentAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO email_verifications (email, code_hash, expires_at, attempts, last_sent_at)
            VALUES ($email, $hash, $expiresAt, 0, $sentAt)
            ON CONFLICT(email) DO UPDATE SET
                code_hash = excluded.code_hash,
                expires_at = excluded.expires_at,
                attempts = 0,
                last_sent_at = excluded.last_sent_at
            """;
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));
        command.Parameters.AddWithValue("$hash", codeHash);
        command.Parameters.AddWithValue("$expiresAt", expiresAt);
        command.Parameters.AddWithValue("$sentAt", sentAt);
        command.ExecuteNonQuery();
    }

    public VerificationRecord? FindVerificationCode(string email)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code_hash, expires_at, attempts, last_sent_at FROM email_verifications WHERE email = $email";
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new VerificationRecord(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    public void IncrementVerificationAttempts(string email)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE email_verifications SET attempts = attempts + 1 WHERE email = $email";
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));
        command.ExecuteNonQuery();
    }

    public void DeleteVerificationCode(string email)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM email_verifications WHERE email = $email";
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));
        command.ExecuteNonQuery();
    }

    /// <summary>管理指标：总量加按套餐的分布（设计 §5 运营视图）。</summary>
    public (int Total, int Free, int Light, int Standard, int ActiveLast30Days) ReadAccountMetrics()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(plan = 0), 0),
                COALESCE(SUM(plan = 1), 0),
                COALESCE(SUM(plan = 2), 0)
            FROM users WHERE disabled = 0
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        var total = reader.GetInt32(0);
        var free = reader.GetInt32(1);
        var light = reader.GetInt32(2);
        var standard = reader.GetInt32(3);
        reader.Close();

        using var activeCommand = connection.CreateCommand();
        activeCommand.CommandText = "SELECT COUNT(DISTINCT user_id) FROM usage_events WHERE created_at >= $since";
        activeCommand.Parameters.AddWithValue("$since", database.NowUnix - (30 * 86_400));
        var active = Convert.ToInt32(activeCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        return (total, free, light, standard, active);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Shorten(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static UserRecord ReadUser(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        (PlanTier)reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.GetInt64(5),
        reader.GetInt32(6) != 0,
        reader.GetString(7));

    private static DeviceRecord ReadDevice(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetInt64(5));
}

public sealed record DeviceResult(bool Succeeded, DeviceRecord? Device, string? Error);

public sealed record VerificationRecord(string CodeHash, long ExpiresAt, int Attempts, long LastSentAt);
