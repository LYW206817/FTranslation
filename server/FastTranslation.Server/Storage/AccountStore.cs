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

/// <summary><paramref name="PeriodStart"/> 是当前个人额度周期的起点日期（yyyy-MM-dd，UTC）。</summary>
public sealed record UsageSnapshot(int MonthlyChars, int DailyChars, string PeriodStart, string Day);

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
            INSERT INTO users (id, email, password_hash, plan, plan_expires_at, created_at, disabled, quota_anchor_at)
            VALUES ($id, $email, $hash, $plan, $planExpiresAt, $createdAt, 0, $anchorAt)
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$email", record.Email);
        command.Parameters.AddWithValue("$hash", record.PasswordHash);
        command.Parameters.AddWithValue("$plan", (int)record.Plan);
        command.Parameters.AddWithValue("$planExpiresAt", (object?)record.PlanExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt);
        command.Parameters.AddWithValue("$anchorAt", QuotaCycle.StartOfUtcDay(record.CreatedAt));
        command.ExecuteNonQuery();
        return record;
    }

    /// <summary>
    /// 授予套餐。<paramref name="expiresAtUnix"/> 为 null 会清除到期时间，这正是
    /// 人工激活码想要的效果：账号一直可用，直到运维另行处置。
    /// </summary>
    public bool SetPlan(Guid userId, PlanTier plan, long? expiresAtUnix = null)
    {
        var now = database.NowUnix;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET plan = $plan,
                plan_expires_at = $expiresAt,
                quota_anchor_at = CASE
                    WHEN plan != 0 AND (plan_expires_at IS NULL OR plan_expires_at > $now)
                        THEN quota_anchor_at
                    ELSE $todayStart
                END
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$plan", (int)plan);
        command.Parameters.AddWithValue("$expiresAt", (object?)expiresAtUnix ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$todayStart", QuotaCycle.StartOfUtcDay(now));
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 把一段时长接到当前订阅之后。读改写放在同一条 UPDATE 里完成：并发兑换两张码时，
    /// 各自基于「请求开始时读到的到期时间」计算绝对时间会让后写覆盖先写，凭空丢一张码的天数。
    /// <para>
    /// v131: 从免费/已失效状态重新开通时重置月度额度锚点；订阅仍有效时续费或升级不动锚点，
    /// 否则提前续费会额外送一段月额度（与 v121「升级不重置用量」是同一口径）。
    /// </para>
    /// </summary>
    public bool ExtendPlan(Guid userId, PlanTier plan, int days, long nowUnix)
    {
        using var connection = database.Open();
        return ExtendPlan(connection, null, userId, plan, days, nowUnix);
    }

    /// <summary>
    /// 在调用方已有事务里延展套餐。订单确认必须和「订单置为 fulfilled」在同一个
    /// SQLite 事务里完成，否则先置订单再改套餐时进程崩在中间，用户付了钱却没有套餐。
    /// </summary>
    internal static bool ExtendPlan(
        SqliteConnection connection, SqliteTransaction? transaction,
        Guid userId, PlanTier plan, int days, long nowUnix)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE users
            SET plan = $plan,
                plan_expires_at = CASE
                    WHEN plan_expires_at IS NOT NULL AND plan_expires_at > $now
                        THEN plan_expires_at + $seconds
                    ELSE $now + $seconds
                END,
                quota_anchor_at = CASE
                    WHEN plan != 0 AND (plan_expires_at IS NULL OR plan_expires_at > $now)
                        THEN quota_anchor_at
                    ELSE $todayStart
                END
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$plan", (int)plan);
        command.Parameters.AddWithValue("$seconds", days * 86_400L);
        command.Parameters.AddWithValue("$now", nowUnix);
        command.Parameters.AddWithValue("$todayStart", QuotaCycle.StartOfUtcDay(nowUnix));
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 在调用方事务里读取账号档位与到期时间，供兑换时在写锁下重做降档判定。
    /// 返回 null 表示账号不存在。
    /// </summary>
    internal static (PlanTier Plan, long? ExpiresAt)? ReadPlan(
        SqliteConnection connection, SqliteTransaction? transaction, Guid userId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT plan, plan_expires_at FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ((PlanTier)reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>禁用与会话吊销同事务提交，解禁只恢复登录能力。</summary>
    public bool SetDisabled(Guid userId, bool disabled)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE users SET disabled = $disabled WHERE id = $id";
        command.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        var changed = command.ExecuteNonQuery() > 0;
        if (changed && disabled)
        {
            RevokeAllRefreshTokens(connection, transaction, userId);
        }
        transaction.Commit();
        return changed;
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

    /// <summary>仅在凭证仍有效时更换密码，并在同一事务内结束旧会话。</summary>
    public bool SetPasswordHash(Guid userId, string expectedHash, string passwordHash)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id AND password_hash = $expected AND disabled = 0";
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$expected", expectedHash);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }
        RevokeAllRefreshTokens(connection, transaction, userId);
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// 彻底删除账号及其个人数据。设备、单词本、月度用量与 refresh token 靠外键
    /// 级联；<c>usage_events</c> 没有外键，必须显式删除。匿名日成本独立保留，
    /// 否则注销会让已付给上游的支出退出熔断统计。兑换码清除账号关联，保留码本身供审计。
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

    /// <summary>今天某个来源已花掉的成本(微元)。日预算按桶分账,必须按 source 单独求和。</summary>
    public long ReadTodayCostMicros(string source) => ReadTodayCosts(source).Settled;

    /// <summary>以同一 SQLite 快照读取预算桶今日成本和仍在发送的预留。</summary>
    public (long Settled, long Pending, long Sending) ReadTodayCosts(string source)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // 转待核对、确认成本和预算检查可能并发，单次读取才能避免漏看状态转换中的支出。
        command.CommandText = """
            SELECT
                COALESCE((SELECT cost_micros FROM gateway_daily_costs WHERE day = $day AND source = $source), 0),
                COALESCE((SELECT SUM(estimated_cost_micros) FROM gateway_dispatches
                    WHERE state = 'pending' AND source = $source AND started_at >= $day AND started_at < $next), 0)
                    + COALESCE((SELECT SUM(estimated_cost_micros) FROM gateway_dispatches
                        WHERE state = 'sending' AND recover_after <= $now AND source = $source
                        AND started_at >= $day AND started_at < $next), 0),
                COALESCE((SELECT SUM(estimated_cost_micros) FROM gateway_dispatches
                    WHERE state = 'sending' AND recover_after > $now AND source = $source), 0)
            """;
        var now = database.NowUnix;
        var day = QuotaCycle.StartOfUtcDay(now);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$next", day + 86_400);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
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
            find.CommandText = "SELECT id, user_id, fingerprint, name, created_at, last_seen_at, deleted_at FROM devices WHERE user_id = $userId AND fingerprint = $fp";
            find.Parameters.AddWithValue("$userId", userId.ToString("D"));
            find.Parameters.AddWithValue("$fp", normalized);

            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadDevice(reader);
                var wasDeleted = !reader.IsDBNull(6);
                reader.Close();

                // v210: 复活软删设备同样要占活跃名额。原实现直接把 deleted_at 置回 NULL，
                // 满 3 台后反复「移除→登录」就能无限挂机，绕过付费共享与按设备的日额度。
                if (wasDeleted && CountActiveDevices(connection, transaction, userId) >= Plans.MaxDevicesPerAccount)
                {
                    transaction.Rollback();
                    return new DeviceResult(false, null, "device_limit_reached");
                }

                // v143：网关侧每次调用 RegisterDevice 都不带客户端名（传空串）；
                // 无条件覆盖会把用户在客户端设置过的设备名抹掉，这里只在有新名字时更新。
                var updated = existing with
                {
                    Name = string.IsNullOrWhiteSpace(name) ? existing.Name : Shorten(name, 64),
                    LastSeenAt = database.NowUnix,
                };

                using var touch = connection.CreateCommand();
                touch.Transaction = transaction;
                touch.CommandText = "UPDATE devices SET last_seen_at = $now, name = $name, deleted_at = NULL WHERE id = $id";
                touch.Parameters.AddWithValue("$now", updated.LastSeenAt);
                touch.Parameters.AddWithValue("$name", updated.Name);
                touch.Parameters.AddWithValue("$id", existing.Id.ToString("D"));
                touch.ExecuteNonQuery();

                transaction.Commit();
                return new DeviceResult(true, updated, null);
            }
        }

        if (CountActiveDevices(connection, transaction, userId) >= Plans.MaxDevicesPerAccount)
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
            FROM devices WHERE user_id = $userId AND fingerprint = $fingerprint AND deleted_at IS NULL
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
        command.CommandText = "SELECT id, user_id, fingerprint, name, created_at, last_seen_at FROM devices WHERE id = $id AND user_id = $userId AND deleted_at IS NULL";
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
            FROM devices WHERE user_id = $userId AND deleted_at IS NULL
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
    /// 消失，旧设备仍能续期，下一次翻译又会把名额占回来。
    /// <para>
    /// v129: 设备行改软删除，usage_daily 保留。硬删会级联清掉设备级日用量，用户用同一
    /// 指纹重新登录就得到一台「今天没用过」的新设备，免费档单设备日限可无限重置。
    /// 重新注册同一指纹时 <see cref="RegisterDevice"/> 会复活这一行。
    /// </para>
    /// </summary>
    public bool DeleteDevice(Guid userId, Guid deviceId)
    {
        var user = userId.ToString("D");
        var device = deviceId.ToString("D");

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        // 先确认设备属于这个账号：不先查就允许用别人的 deviceId 删掉对方的设备。
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

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "UPDATE devices SET deleted_at = $now WHERE id = $deviceId AND user_id = $userId AND deleted_at IS NULL";
            delete.Parameters.AddWithValue("$now", database.NowUnix);
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
        var periodStart = QuotaPeriodKey(connection, null, userId);

        int monthly = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT chars FROM usage_monthly WHERE user_id = $userId AND month = $month";
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
            command.Parameters.AddWithValue("$month", periodStart);
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

        return new UsageSnapshot(monthly, daily, periodStart, database.TodayKey);
    }

    /// <summary>
    /// 当前个人周期的键（yyyy-MM-dd）。账号不存在时锚点为 0，周期退化为当月 1 日；
    /// 未知账号的用量本来就是 0，不会因为这条回退产生额度。
    /// </summary>
    private string QuotaPeriodKey(SqliteConnection connection, SqliteTransaction? transaction, Guid userId)
        => QuotaCycle.Key(ReadQuotaAnchor(connection, transaction, userId) ?? 0L, database.NowUnix);

    private static long? ReadQuotaAnchor(SqliteConnection connection, SqliteTransaction? transaction, Guid userId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT quota_anchor_at FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return command.ExecuteScalar() as long?;
    }

    /// <summary>在 SQLite 写事务内检查余额并为本次请求占位。</summary>
    public QuotaPolicy.Decision TryReserveQuota(Guid userId, Guid deviceId, int chars, PlanTier tier, out Guid reservationId)
    {
        reservationId = default;
        using var connection = database.Open();
        // v198: 读余额到写入凭据期间持有写锁，拒绝结果也取自同一快照。
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = database.NowUnix;
        var anchor = ReadQuotaAnchor(connection, transaction, userId);
        if (anchor is null || deviceId == Guid.Empty)
        {
            return QuotaPolicy.Evaluate(tier, 0, 0, chars) with { Allowed = false, Reason = "account_disabled" };
        }

        var month = QuotaCycle.Key(anchor.Value, now);
        var day = UtcDayKey(now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$month", month);
        command.Parameters.AddWithValue("$day", day);
        command.CommandText = """
            SELECT
                COALESCE((SELECT chars FROM usage_monthly WHERE user_id = $userId AND month = $month), 0)
                + COALESCE((SELECT SUM(chars) FROM quota_reservations WHERE user_id = $userId AND month = $month), 0),
                COALESCE((SELECT chars FROM usage_daily WHERE device_id = $deviceId AND day = $day), 0)
                + COALESCE((SELECT SUM(chars) FROM quota_reservations WHERE device_id = $deviceId AND day = $day), 0)
            """;
        QuotaPolicy.Decision decision;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            decision = QuotaPolicy.Evaluate(tier, reader.GetInt32(0), reader.GetInt32(1), chars);
        }

        if (!decision.Allowed)
        {
            return decision;
        }

        var id = Guid.NewGuid();
        command.CommandText = """
            INSERT INTO quota_reservations (id, user_id, device_id, month, day, chars)
            VALUES ($id, $userId, $deviceId, $month, $day, $chars)
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$chars", chars);
        command.ExecuteNonQuery();
        transaction.Commit();
        reservationId = id;
        return decision;
    }

    /// <summary>释放该请求的剩余额度，重复释放没有副作用。</summary>
    public void ReleaseQuota(Guid reservationId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quota_reservations WHERE id = $id";
        command.Parameters.AddWithValue("$id", reservationId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static string UtcDayKey(long now) =>
        DateTimeOffset.FromUnixTimeSeconds(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static (string Month, string Day)? ConsumeQuota(
        SqliteConnection connection, SqliteTransaction transaction, Guid reservationId, int chars, bool complete)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", reservationId.ToString("D"));
        command.Parameters.AddWithValue("$chars", chars);
        // v198: 语音增量用实耗替换预留，翻译一次结算后直接作废凭据，重入时不会重复扣费。
        command.CommandText = complete
            ? "DELETE FROM quota_reservations WHERE id = $id RETURNING month, day"
            : "UPDATE quota_reservations SET chars = MAX(0, chars - $chars) WHERE id = $id RETURNING month, day";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>
    /// 记一笔用量。<paramref name="source"/> 是预算桶名（<see cref="Gateway.GatewayBuckets"/>），
    /// 没有默认值：漏传就会把会员或语音的账记进免费桶，熔断与展示都会错。
    /// </summary>
    public bool RecordUsage(Guid userId, Guid deviceId, int chars, string model, int tokensIn, int tokensOut,
        long costMicros, long now, string source, Guid? reservationId = null, bool completeReservation = true,
        GatewayUsageCompletion? gatewayCompletion = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (gatewayCompletion is { } completion
            && !GatewaySettlementStore.Complete(connection, transaction, completion.Id, completion.UpstreamRequestId, now))
        {
            return false;
        }

        using var entry = connection.CreateCommand();
        entry.Transaction = transaction;
        entry.Parameters.AddWithValue("$chars", chars);
        entry.Parameters.AddWithValue("$tokensIn", tokensIn);
        entry.Parameters.AddWithValue("$tokensOut", tokensOut);
        entry.Parameters.AddWithValue("$cost", costMicros);
        entry.Parameters.AddWithValue("$source", source);
        entry.Parameters.AddWithValue("$now", now);

        var anchor = ReadQuotaAnchor(connection, transaction, userId);
        if (anchor is null)
        {
            // v197: 请求在用户注销前已发给上游时，仍需入成本账，但不能重建个人记录。
            RecordAnonymousUsage(connection, transaction, source, costMicros, now, 1, chars, tokensIn, tokensOut);
            transaction.Commit();
            return true;
        }

        var window = reservationId is { } id
            ? ConsumeQuota(connection, transaction, id, chars, completeReservation)
            : (QuotaCycle.Key(anchor.Value, now), UtcDayKey(now));
        if (window is not { } quotaWindow)
        {
            return false;
        }

        using (var monthly = connection.CreateCommand())
        {
            monthly.Transaction = transaction;
            monthly.CommandText = """
                INSERT INTO usage_monthly (user_id, month, chars) VALUES ($userId, $month, $chars)
                ON CONFLICT(user_id, month) DO UPDATE SET chars = chars + excluded.chars
                """;
            monthly.Parameters.AddWithValue("$userId", userId.ToString("D"));
            monthly.Parameters.AddWithValue("$month", quotaWindow.Item1);
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
            daily.Parameters.AddWithValue("$day", quotaWindow.Item2);
            daily.Parameters.AddWithValue("$chars", chars);
            daily.ExecuteNonQuery();
        }

        entry.CommandText = """
            INSERT INTO usage_events (user_id, device_id, model, chars, tokens_in, tokens_out, cost_micros, source, created_at)
            VALUES ($userId, $deviceId, $model, $chars, $tokensIn, $tokensOut, $cost, $source, $now)
            """;
        entry.Parameters.AddWithValue("$userId", userId.ToString("D"));
        entry.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
        entry.Parameters.AddWithValue("$model", model);
        entry.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    internal static void RecordAnonymousUsage(SqliteConnection connection, SqliteTransaction transaction,
        string source, long costMicros, long now, int requests, int chars = 0, int tokensIn = 0, int tokensOut = 0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gateway_daily_costs (day, source, requests, chars, tokens_in, tokens_out, cost_micros)
            VALUES ($day, $source, $requests, $chars, $tokensIn, $tokensOut, $cost)
            ON CONFLICT(day, source) DO UPDATE SET
                requests = requests + excluded.requests,
                chars = chars + excluded.chars,
                tokens_in = tokens_in + excluded.tokens_in,
                tokens_out = tokens_out + excluded.tokens_out,
                cost_micros = cost_micros + excluded.cost_micros
            """;
        command.Parameters.AddWithValue("$day", QuotaCycle.StartOfUtcDay(now));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$requests", requests);
        command.Parameters.AddWithValue("$chars", chars);
        command.Parameters.AddWithValue("$tokensIn", tokensIn);
        command.Parameters.AddWithValue("$tokensOut", tokensOut);
        command.Parameters.AddWithValue("$cost", costMicros);
        command.ExecuteNonQuery();
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

    /// <summary>仅为仍有效的账号、密码与设备创建登录链。</summary>
    public bool CreateSession(UserRecord user, Guid deviceId, Guid sessionId, string tokenHash, long expiresAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // 登录校验后的并发改密或移除设备不能拿着旧快照创建有效会话。
        command.CommandText = """
            INSERT INTO refresh_tokens (id, user_id, device_id, session_id, token_hash, created_at, expires_at)
            SELECT $id, u.id, d.id, $session, $hash, $now, $expires
            FROM users u JOIN devices d ON d.user_id = u.id
            WHERE u.id = $user AND d.id = $device AND u.disabled = 0 AND d.deleted_at IS NULL
              AND u.password_hash = $password
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$user", user.Id.ToString("D"));
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", database.NowUnix);
        command.Parameters.AddWithValue("$expires", expiresAt);
        command.Parameters.AddWithValue("$password", user.PasswordHash);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>返回这条登录链当前绑定的有效设备。</summary>
    public Guid? FindSessionDevice(Guid userId, Guid sessionId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id FROM refresh_tokens r JOIN devices d ON d.id = r.device_id
            JOIN users u ON u.id = r.user_id
            WHERE r.session_id = $session AND u.id = $user AND r.revoked_at IS NULL
              AND r.expires_at > $now AND u.disabled = 0 AND d.deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$now", database.NowUnix);
        return Guid.TryParse(command.ExecuteScalar() as string, out var deviceId) ? deviceId : null;
    }

    /// <summary>一次性消费刷新令牌。</summary>
    public RefreshTokenOwner? ConsumeRefreshToken(string tokenHash) => ExchangeRefreshToken(tokenHash, null, 0);

    /// <summary>在同一事务内作废旧令牌并写入同一登录链的替代令牌。</summary>
    public RefreshTokenOwner? RotateRefreshToken(string tokenHash, string replacementHash, long expiresAt)
        => ExchangeRefreshToken(tokenHash, replacementHash, expiresAt);

    private RefreshTokenOwner? ExchangeRefreshToken(string tokenHash, string? replacementHash, long expiresAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        Guid userId;
        Guid? deviceId;
        Guid? sessionId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT user_id, device_id, session_id FROM refresh_tokens
                WHERE token_hash = $hash AND revoked_at IS NULL AND expires_at > $now
                  AND ($consume = 1 OR EXISTS (
                      SELECT 1 FROM users u JOIN devices d ON d.user_id = u.id
                      WHERE u.id = refresh_tokens.user_id AND d.id = refresh_tokens.device_id
                        AND u.disabled = 0 AND d.deleted_at IS NULL))
                """;
            select.Parameters.AddWithValue("$hash", tokenHash);
            select.Parameters.AddWithValue("$now", database.NowUnix);
            select.Parameters.AddWithValue("$consume", replacementHash is null ? 1 : 0);

            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read() || !Guid.TryParse(reader.GetString(0), out userId))
                {
                    transaction.Rollback();
                    return null;
                }

                deviceId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
                sessionId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
            }
        }

        if (replacementHash is not null && sessionId is null)
        {
            return null;
        }

        using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE refresh_tokens SET revoked_at = $now WHERE token_hash = $hash";
            revoke.Parameters.AddWithValue("$hash", tokenHash);
            revoke.Parameters.AddWithValue("$now", database.NowUnix);
            revoke.ExecuteNonQuery();
        }

        if (replacementHash is not null)
        {
            using var replace = connection.CreateCommand();
            replace.Transaction = transaction;
            // v200: 设备移除或改密必须排在整个轮换前或后，否则会漏掉尚未写入的替代凭据。
            replace.CommandText = """
                INSERT INTO refresh_tokens (id, user_id, device_id, session_id, token_hash, created_at, expires_at)
                SELECT $id, user_id, device_id, session_id, $newHash, $now, $expires
                FROM refresh_tokens WHERE token_hash = $oldHash
                """;
            replace.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            replace.Parameters.AddWithValue("$newHash", replacementHash);
            replace.Parameters.AddWithValue("$oldHash", tokenHash);
            replace.Parameters.AddWithValue("$now", database.NowUnix);
            replace.Parameters.AddWithValue("$expires", expiresAt);
            replace.ExecuteNonQuery();
        }

        transaction.Commit();
        return new RefreshTokenOwner(userId, deviceId, sessionId);
    }

    public int RevokeAllRefreshTokens(Guid userId)
    {
        using var connection = database.Open();
        return RevokeAllRefreshTokens(connection, null, userId);
    }

    private int RevokeAllRefreshTokens(SqliteConnection connection, SqliteTransaction? transaction, Guid userId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    /// <summary>管理端从 UTC 日起点聚合匿名用量，成本以微元为单位。</summary>
    public (long Requests, long Chars, long TokensIn, long TokensOut, long CostMicros) ReadUsageTotals(long fromUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(requests), 0), COALESCE(SUM(chars), 0), COALESCE(SUM(tokens_in), 0),
                   COALESCE(SUM(tokens_out), 0), COALESCE(SUM(cost_micros), 0)
            FROM gateway_daily_costs WHERE day >= $from
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
            SELECT source, SUM(requests), SUM(chars), SUM(cost_micros)
            FROM gateway_daily_costs WHERE day >= $from
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
            SELECT strftime('%Y-%m-%d', day, 'unixepoch'),
                   SUM(requests), SUM(chars), SUM(cost_micros)
            FROM gateway_daily_costs WHERE day >= $from
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
                   u.quota_anchor_at, u.id
            FROM users u
            {where}
            ORDER BY u.created_at DESC, u.email ASC
            LIMIT $limit OFFSET $offset
            """;
        AddAccountFilters(command, prefix, plan);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<(AdminAccountRow Row, Guid Id, long Anchor)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    new AdminAccountRow(
                        reader.GetString(0),
                        (PlanTier)reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetInt32(3) != 0,
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        0),
                    Guid.Parse(reader.GetString(7)),
                    reader.GetInt64(6)));
            }
        }

        // 每个账号的「本周期字符」按各自的锚点算；一页最多 50 行，逐行查一次
        // 比在 SQLite 里复刻 AddMonths 的月末夹取逻辑更不容易出错。
        var items = new List<AdminAccountRow>(rows.Count);
        foreach (var (row, id, anchor) in rows)
        {
            using var usage = connection.CreateCommand();
            usage.CommandText = "SELECT chars FROM usage_monthly WHERE user_id = $userId AND month = $month";
            usage.Parameters.AddWithValue("$userId", id.ToString("D"));
            usage.Parameters.AddWithValue("$month", QuotaCycle.Key(anchor, database.NowUnix));
            var chars = usage.ExecuteScalar() is { } value
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : 0;
            items.Add(row with { MonthlyChars = chars });
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

    private static int CountActiveDevices(SqliteConnection connection, SqliteTransaction transaction, Guid userId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM devices WHERE user_id = $userId AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

public sealed record DeviceResult(bool Succeeded, DeviceRecord? Device, string? Error);

/// <summary>一条 refresh token 属于哪个账号、哪台设备（旧令牌没有设备）。</summary>
public sealed record RefreshTokenOwner(Guid UserId, Guid? DeviceId, Guid? SessionId);

public sealed record VerificationRecord(string CodeHash, long ExpiresAt, int Attempts, long LastSentAt, string Purpose);
