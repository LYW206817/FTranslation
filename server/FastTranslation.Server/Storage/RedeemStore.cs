using FastTranslation.Server.Domain;

namespace FastTranslation.Server.Storage;

/// <summary>
/// 一个已售出的激活码。<paramref name="Days"/> 是相对天数，因为一批码是按
/// 时长定价的；绝对到期时间只有在有人兑换时才确定。
/// </summary>
public sealed record RedeemCodeRecord(
    string Code,
    PlanTier Plan,
    int Days,
    string Note,
    long CreatedAt,
    Guid? RedeemedBy,
    long? RedeemedAt,
    long? RevokedAt)
{
    public bool IsRedeemed => RedeemedAt is not null;
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// 激活码账本。码由运维写入一次，由某个用户恰好兑换一次，所以
/// 唯一需要处理的并发是“两个客户端同时兑换同一个码”——
/// 这里用带条件的 UPDATE 抢占来处理，而不是在读取外再包一层
/// 事务。
/// </summary>
public sealed class RedeemStore(ServerDatabase database)
{
    public void CreateCodes(IReadOnlyList<string> codes, PlanTier plan, int days, string note, long now)
    {
        if (codes.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var code in codes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO redeem_codes (code, plan, days, note, created_at)
                VALUES ($code, $plan, $days, $note, $createdAt)
                """;
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$plan", (int)plan);
            command.Parameters.AddWithValue("$days", days);
            command.Parameters.AddWithValue("$note", note);
            command.Parameters.AddWithValue("$createdAt", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public RedeemCodeRecord? Find(string code)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, plan, days, note, created_at, redeemed_by, redeemed_at, revoked_at
            FROM redeem_codes WHERE code = $code
            """;
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RedeemCodeRecord(
            reader.GetString(0),
            (PlanTier)reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7));
    }

    /// <summary>只有第一个调用者拿到 true：正是 WHERE 子句让重复兑换不可能发生。</summary>
    public bool TryClaim(string code, Guid userId, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE redeem_codes SET redeemed_by = $userId, redeemed_at = $now
            WHERE code = $code AND redeemed_at IS NULL AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$code", code);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 把码标记为作废。码未知或已兑换时返回 false：已兑付的码
    /// 是客户的财产，只有尚未被兑换的码才能收回。
    /// </summary>
    public bool Revoke(string code, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE redeem_codes SET revoked_at = $now
            WHERE code = $code AND redeemed_at IS NULL AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$code", code);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>运维账本视图的一行；显示买家邮箱，而不是用户 id。</summary>
    public sealed record LedgerRow(
        string Code,
        string Plan,
        int Days,
        string Note,
        long CreatedAt,
        string? RedeemedEmail,
        long? RedeemedAt,
        long? RevokedAt);

    public static bool IsKnownStatus(string status) =>
        status is "open" or "used" or "revoked" or "all";

    /// <summary>
    /// 码按创建时间倒序，给弄丢了生成接口那批码的运维看。只有从未
    /// 交付给买家的码才能作废，所以正是这个列表让录错的一批
    /// 可以挽回，而不是白扔。
    /// </summary>
    public IReadOnlyList<LedgerRow> List(string status, int take, int skip)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        const string Head = """
            SELECT r.code, r.plan, r.days, r.note, r.created_at, u.email, r.redeemed_at, r.revoked_at
            FROM redeem_codes AS r
            LEFT JOIN users AS u ON u.id = r.redeemed_by
            """;
        // 空格是显式写出来的，而不是依赖原始字符串字面量的规则：原始字面量会丢掉
        // 结尾引号前的换行，而 "IS NULLORDER BY" 是语法错误，不是笔误。
        const string Tail = " ORDER BY r.created_at DESC, r.code LIMIT $take OFFSET $skip";
        command.CommandText = Head + Where(status) + Tail;
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        using var reader = command.ExecuteReader();
        var rows = new List<LedgerRow>();
        while (reader.Read())
        {
            rows.Add(new LedgerRow(
                reader.GetString(0),
                ((PlanTier)reader.GetInt32(1)).ToString().ToLowerInvariant(),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return rows;
    }

    public int Count(string status)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM redeem_codes AS r" + Where(status);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 状态过滤只写一处，这样 List 与 Count 对“open”的定义
    /// 永远不会不一致。表只有几百行，不值得为这次扫描建索引。
    /// </summary>
    private static string Where(string status) => status switch
    {
        "used" => " WHERE r.redeemed_at IS NOT NULL",
        "revoked" => " WHERE r.revoked_at IS NOT NULL",
        "all" => string.Empty,
        _ => " WHERE r.redeemed_at IS NULL AND r.revoked_at IS NULL",
    };

    public (int Total, int Redeemed) ReadStats()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(redeemed_at) FROM redeem_codes";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }
}
