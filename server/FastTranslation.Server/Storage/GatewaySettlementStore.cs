using System.Security.Cryptography;
using FastTranslation.Server.Domain;
using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

/// <summary>一次向供应商发送的成本记录，仅保留对账元数据。</summary>
public sealed record PendingGatewayCharge(Guid Id, string Source, long EstimatedCostMicros, long StartedAtUnix, string? UpstreamRequestId);

/// <summary>与成功用量在同一事务落盘的上游发送回执。</summary>
public readonly record struct GatewayUsageCompletion(Guid Id, string? UpstreamRequestId);

/// <summary>在发送前持久化请求，保证进程退出后仍能查到未确认的成本。</summary>
public sealed class GatewaySettlementStore(ServerDatabase database, GatewayOptions options)
{
    private const long SecondsPerDay = 86_400;
    private const int CompletedRetentionDays = 30;
    private const int RecoveryGraceSeconds = 120;
    private readonly object _cleanupGate = new();
    private long _lastCleanupDay = long.MinValue;

    /// <summary>按账号隔离客户端请求标识，数据库只保存摘要。</summary>
    public static string GetOperationKey(Guid userId, Guid operationId)
    {
        Span<byte> identity = stackalloc byte[32];
        userId.TryWriteBytes(identity[..16]);
        operationId.TryWriteBytes(identity[16..]);
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    /// <summary>检查重试请求是否已登记。</summary>
    public bool HasOperation(string key)
    {
        using var connection = database.Open();
        PruneCompleted(connection, database.NowUnix);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM gateway_dispatches WHERE operation_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>只有首个登记成功的请求可以发送给供应商。</summary>
    public bool TryBegin(Guid id, string? key, string source, long estimatedCostMicros)
    {
        using var connection = database.Open();
        var now = database.NowUnix;
        PruneCompleted(connection, now);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gateway_dispatches (id, operation_key, source, estimated_cost_micros, started_at, recover_after, state)
            VALUES ($id, $key, $source, $estimate, $now, $recover_after, 'sending')
            ON CONFLICT(operation_key) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$estimate", estimatedCostMicros);
        command.Parameters.AddWithValue("$now", now);
        // 发送有独立截止时间；另外留出 SQLite 提交与释放时间。进程仍存活而最终写入
        // 失败时，过期的发送预留仍占预算，并且可进入对账队列。
        command.Parameters.AddWithValue("$recover_after", now + options.EffectiveTimeoutSeconds + RecoveryGraceSeconds);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>确认拒绝后允许同一个客户端动作按原策略重试。</summary>
    public void Reject(Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM gateway_dispatches WHERE id = $id AND state = 'sending'";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    /// <summary>将尚未结算的发送标为待核对。</summary>
    public void MarkPending(Guid id, string? upstreamRequestId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE gateway_dispatches SET state = 'pending', upstream_request_id = $upstream
            WHERE id = $id AND state = 'sending'
            """;
        BindReference(command, id, upstreamRequestId);
        command.ExecuteNonQuery();
    }

    internal static bool Complete(
        SqliteConnection connection, SqliteTransaction transaction, Guid id, string? upstreamRequestId, long completedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 只有这笔发送的第一位完成者能写入用量。状态和用量随同一个事务提交。
        command.CommandText = """
            UPDATE gateway_dispatches
            SET state = 'settled', upstream_request_id = $upstream, completed_at = $completed
            WHERE id = $id AND state = 'sending'
            """;
        BindReference(command, id, upstreamRequestId);
        command.Parameters.AddWithValue("$completed", completedAt);
        if (command.ExecuteNonQuery() != 1) return false;
        // 旧客户端没有跨请求重试标识，已结算后只需保留汇总账。
        command.CommandText = "DELETE FROM gateway_dispatches WHERE id = $id AND operation_key IS NULL";
        command.ExecuteNonQuery();
        return true;
    }

    /// <summary>查询尚未核对的账目，包含已跨日的记录。</summary>
    public IReadOnlyList<PendingGatewayCharge> ReadPending(int limit, int offset)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, estimated_cost_micros, started_at, upstream_request_id
            FROM gateway_dispatches
            WHERE state = 'pending' OR (state = 'sending' AND recover_after <= $now)
            ORDER BY started_at, id LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$now", database.NowUnix);
        using var reader = command.ExecuteReader();
        var charges = new List<PendingGatewayCharge>();
        while (reader.Read())
        {
            charges.Add(new PendingGatewayCharge(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return charges;
    }

    /// <summary>按已核对的供应商费用更新平台成本，避免把未交付结果扣到个人用量。</summary>
    public bool Resolve(Guid id, long costMicros)
    {
        using var connection = database.Open();
        var now = database.NowUnix;
        PruneCompleted(connection, now);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, source, started_at, resolved_cost_micros, recover_after FROM gateway_dispatches WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        string source;
        long startedAt;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return false;
            var state = reader.GetString(0);
            if (state == "reconciled") return reader.GetInt64(3) == costMicros;
            if (state != "pending" && (state != "sending" || reader.GetInt64(4) > now)) return false;
            source = reader.GetString(1);
            startedAt = reader.GetInt64(2);
        }

        command.CommandText = """
            UPDATE gateway_dispatches SET state = 'reconciled', resolved_cost_micros = $cost, completed_at = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$cost", costMicros);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        if (costMicros > 0)
        {
            AccountStore.RecordAnonymousUsage(connection, transaction, source, costMicros, startedAt, requests: 0);
        }
        transaction.Commit();
        return true;
    }

    private void PruneCompleted(SqliteConnection connection, long now)
    {
        var day = QuotaCycle.StartOfUtcDay(now);
        if (Volatile.Read(ref _lastCleanupDay) >= day) return;
        lock (_cleanupGate)
        {
            if (_lastCleanupDay >= day) return;
            using var command = connection.CreateCommand();
            // 到期只清除终态明细，日成本和待核对记录各自承担不同的保留职责。
            command.CommandText = "DELETE FROM gateway_dispatches WHERE completed_at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", day - CompletedRetentionDays * SecondsPerDay);
            command.ExecuteNonQuery();
            Volatile.Write(ref _lastCleanupDay, day);
        }
    }

    private static void BindReference(SqliteCommand command, Guid id, string? upstreamRequestId)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$upstream", (object?)upstreamRequestId ?? DBNull.Value);
    }
}
