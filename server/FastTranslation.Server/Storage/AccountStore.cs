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

/// <summary>管理端账号列表的一行。Plan 是存储值；过期折算在端点层做。</summary>
public sealed record AdminAccountRow(
    string Email,
    PlanTier Plan,
    long? PlanExpiresAt,
    bool Disabled,
    long CreatedAt,
    long LastSeenAt,
    int MonthlyChars);

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
    /// 把一段时长接到当前订阅之后。读改写放在同一条 UPDATE 里完成：并发兑换两张码时，
    /// 各自基于「请求开始时读到的到期时间」计算绝对时间会让后写覆盖先写，凭空丢一张码的天数。
    /// </summary>
    public bool ExtendPlan(Guid userId, PlanTier plan, int days, long nowUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET plan = $plan,
                plan_expires_at = CASE
                    WHEN plan_expires_at IS NOT NULL AND plan_expires_at > $now
                        THEN plan_expires_at + $seconds
                    ELSE $now + $seconds
                END
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$plan", (int)plan);
        command.Parameters.AddWithValue("$seconds", days * 86_400L);
        command.Parameters.AddWithValue("$now", nowUnix);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>禁用/解禁账号。禁用时由调用方同时吊销 refresh token。</summary>
    public bool SetDisabled(Guid userId, bool disabled)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET disabled = $disabled WHERE id = $id";
        command.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
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

    /// <summary>
    /// 彻底删除账号及其个人数据。设备、单词本、月度用量与 refresh token 靠外键
    /// 级联；<c>usage_events</c> 刻意没有外键（它要独立于账号保留一段时间做对账），
    /// 所以必须显式删。兑换码只把 <c>redeemed_by</c> 置空，码本身留作审计。
    /// </summary>
    public bool DeleteUser(Guid userId, string email)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
        {
            "DELETE FROM usage_events WHERE user_id = $userId",
            "DELETE FROM email_verifications WHERE email = $email",
        })
        {
            using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = sql;
            cleanup.Parameters.AddWithValue("$userId", userId.ToString("D"));
            cleanup.Parameters.AddWithValue("$email", NormalizeEmail(email));
            cleanup.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM users WHERE id = $userId";
        delete.Parameters.AddWithValue("$userId", userId.ToString("D"));
        var deleted = delete.ExecuteNonQuery() > 0;

        transaction.Commit();
        return deleted;
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

    /// <summary>账号页的设备列表，最近登记的排在前面。</summary>
    public IReadOnlyList<DeviceRecord> ListDevices(Guid userId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, fingerprint, name, created_at, last_seen_at
            FROM devices WHERE user_id = $userId
            ORDER BY last_seen_at DESC, created_at DESC
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));

        using var reader = command.ExecuteReader();
        var devices = new List<DeviceRecord>();
        while (reader.Read()) devices.Add(ReadDevice(reader));
        return devices;
    }

    /// <summary>
    /// 移除一台设备。同一设备上的 refresh token 必须一起吊销：否则它只是从列表里
    /// 消失，旧设备仍能续期，下一次翻译又会把名额占回来。设备级日用量也一并清掉。
    ///
    /// <para>
    /// SQLite 的 <c>foreign_keys</c> 是连接级 pragma，而这里每次操作都用新连接，
    /// schema 里的 <c>ON DELETE CASCADE</c> 不会生效——子行必须显式删。
    /// </para>
    /// </summary>
    public bool DeleteDevice(Guid userId, Guid deviceId)
    {
        var user = userId.ToString("D");
        var device = deviceId.ToString("D");

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        // 先确认设备属于这个账号：不先查就直接删 usage_daily，会允许用别人的
        // deviceId 抹掉对方的日用量，即使随后的 devices 删除返回 0。
        using (var owned = connection.CreateCommand())
        {
            owned.Transaction = transaction;
            owned.CommandText = "SELECT 1 FROM devices WHERE id = $deviceId AND user_id = $userId";
            owned.Parameters.AddWithValue("$deviceId", device);
            owned.Parameters.AddWithValue("$userId", user);
            if (owned.ExecuteScalar() is null)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE refresh_tokens SET revoked_at = $now
                WHERE user_id = $userId AND device_id = $deviceId AND revoked_at IS NULL
                """;
            revoke.Parameters.AddWithValue("$now", database.NowUnix);
            revoke.Parameters.AddWithValue("$userId", user);
            revoke.Parameters.AddWithValue("$deviceId", device);
            revoke.ExecuteNonQuery();
        }

        using (var daily = connection.CreateCommand())
        {
            daily.Transaction = transaction;
            daily.CommandText = "DELETE FROM usage_daily WHERE device_id = $deviceId";
            daily.Parameters.AddWithValue("$deviceId", device);
            daily.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM devices WHERE id = $deviceId AND user_id = $userId";
            delete.Parameters.AddWithValue("$deviceId", device);
            delete.Parameters.AddWithValue("$userId", user);
            if (delete.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        return true;
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

    public void SaveRefreshToken(Guid userId, string tokenHash, long expiresAt, Guid? deviceId = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO refresh_tokens (id, user_id, device_id, token_hash, created_at, expires_at, revoked_at)
            VALUES ($id, $userId, $deviceId, $hash, $now, $expiresAt, NULL)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$deviceId", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", database.NowUnix);
        command.Parameters.AddWithValue("$expiresAt", expiresAt);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 消费一个 refresh token（一次性轮换）。返回所属账号与设备；设备为 null
    /// 表示 v102 之前签发的旧令牌，轮换后也继续保持 null。无效时返回 null。
    /// </summary>
    public RefreshTokenOwner? ConsumeRefreshToken(string tokenHash)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        Guid userId;
        Guid? deviceId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT user_id, device_id FROM refresh_tokens
                WHERE token_hash = $hash AND revoked_at IS NULL AND expires_at > $now
                """;
            select.Parameters.AddWithValue("$hash", tokenHash);
            select.Parameters.AddWithValue("$now", database.NowUnix);

            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read() || !Guid.TryParse(reader.GetString(0), out userId))
                {
                    transaction.Rollback();
                    return null;
                }

                deviceId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
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
        return new RefreshTokenOwner(userId, deviceId);
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

    public void SaveVerificationCode(string email, string codeHash, long expiresAt, long sentAt, string purpose)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO email_verifications (email, code_hash, expires_at, attempts, last_sent_at, purpose)
            VALUES ($email, $hash, $expiresAt, 0, $sentAt, $purpose)
            ON CONFLICT(email) DO UPDATE SET
                code_hash = excluded.code_hash,
                expires_at = excluded.expires_at,
                attempts = 0,
                last_sent_at = excluded.last_sent_at,
                purpose = excluded.purpose
            """;
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));
        command.Parameters.AddWithValue("$hash", codeHash);
        command.Parameters.AddWithValue("$expiresAt", expiresAt);
        command.Parameters.AddWithValue("$sentAt", sentAt);
        command.Parameters.AddWithValue("$purpose", purpose);
        command.ExecuteNonQuery();
    }

    public VerificationRecord? FindVerificationCode(string email)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code_hash, expires_at, attempts, last_sent_at, purpose FROM email_verifications WHERE email = $email";
        command.Parameters.AddWithValue("$email", NormalizeEmail(email));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new VerificationRecord(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetString(4));
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
    public (int Total, int Free, int Light, int Standard, int Max, int ActiveLast30Days) ReadAccountMetrics()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(plan = 0), 0),
                COALESCE(SUM(plan = 1), 0),
                COALESCE(SUM(plan = 2), 0),
                COALESCE(SUM(plan = 3), 0)
            FROM users WHERE disabled = 0
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        var total = reader.GetInt32(0);
        var free = reader.GetInt32(1);
        var light = reader.GetInt32(2);
        var standard = reader.GetInt32(3);
        var max = reader.GetInt32(4);
        reader.Close();

        using var activeCommand = connection.CreateCommand();
        activeCommand.CommandText = "SELECT COUNT(DISTINCT user_id) FROM usage_events WHERE created_at >= $since";
        activeCommand.Parameters.AddWithValue("$since", database.NowUnix - (30 * 86_400));
        var active = Convert.ToInt32(activeCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        return (total, free, light, standard, max, active);
    }

    /// <summary>管理端的全局时间窗聚合；cost 仍以微元为单位。</summary>
    public (long Requests, long Chars, long TokensIn, long TokensOut, long CostMicros) ReadUsageTotals(long fromUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(chars), 0), COALESCE(SUM(tokens_in), 0),
                   COALESCE(SUM(tokens_out), 0), COALESCE(SUM(cost_micros), 0)
            FROM usage_events WHERE created_at >= $from
            """;
        command.Parameters.AddWithValue("$from", fromUnix);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<(string Source, long Requests, long Chars, long CostMicros)> ReadUsageBySource(long fromUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source, COUNT(*), COALESCE(SUM(chars), 0), COALESCE(SUM(cost_micros), 0)
            FROM usage_events WHERE created_at >= $from
            GROUP BY source ORDER BY source
            """;
        command.Parameters.AddWithValue("$from", fromUnix);

        using var reader = command.ExecuteReader();
        var rows = new List<(string Source, long Requests, long Chars, long CostMicros)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    public IReadOnlyList<(string Day, long Requests, long Chars, long CostMicros)> ReadUsageByDay(long fromUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT strftime('%Y-%m-%d', created_at, 'unixepoch') AS day,
                   COUNT(*), COALESCE(SUM(chars), 0), COALESCE(SUM(cost_micros), 0)
            FROM usage_events WHERE created_at >= $from
            GROUP BY day ORDER BY day
            """;
        command.Parameters.AddWithValue("$from", fromUnix);

        using var reader = command.ExecuteReader();
        var rows = new List<(string Day, long Requests, long Chars, long CostMicros)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>
    /// 管理端账号分页。query 只做邮箱前缀匹配，<c>%</c>/<c>_</c> 会被转义，
    /// 避免一个 <c>%</c> 变成全表扫描的「查所有」。
    /// </summary>
    public (IReadOnlyList<AdminAccountRow> Items, int Total) ReadAccountPage(
        string? query,
        PlanTier? plan,
        int limit,
        int offset)
    {
        var filters = new List<string>();
        var prefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filters.Add("u.email LIKE $prefix ESCAPE '\\'");
            prefix = EscapeLike(query.Trim()) + "%";
        }
        if (plan is not null)
        {
            filters.Add("u.plan = $plan");
        }
        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

        using var connection = database.Open();

        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM users u {where}";
            AddAccountFilters(count, prefix, plan);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.email, u.plan, u.plan_expires_at, u.disabled, u.created_at,
                   COALESCE((SELECT MAX(d.last_seen_at) FROM devices d WHERE d.user_id = u.id), 0),
                   COALESCE((SELECT m.chars FROM usage_monthly m WHERE m.user_id = u.id AND m.month = $month), 0)
            FROM users u
            {where}
            ORDER BY u.created_at DESC, u.email ASC
            LIMIT $limit OFFSET $offset
            """;
        AddAccountFilters(command, prefix, plan);
        command.Parameters.AddWithValue("$month", database.ThisMonthKey);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        using var reader = command.ExecuteReader();
        var items = new List<AdminAccountRow>();
        while (reader.Read())
        {
            items.Add(new AdminAccountRow(
                reader.GetString(0),
                (PlanTier)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt32(3) != 0,
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6)));
        }

        return (items, total);
    }

    private static void AddAccountFilters(SqliteCommand command, string prefix, PlanTier? plan)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            command.Parameters.AddWithValue("$prefix", prefix);
        }
        if (plan is { } tier)
        {
            command.Parameters.AddWithValue("$plan", (int)tier);
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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

/// <summary>一条 refresh token 属于哪个账号、哪台设备（旧令牌没有设备）。</summary>
public sealed record RefreshTokenOwner(Guid UserId, Guid? DeviceId);

public sealed record VerificationRecord(string CodeHash, long ExpiresAt, int Attempts, long LastSentAt, string Purpose);
