using FastTranslation.Server.Domain;

namespace FastTranslation.Server.Storage;

/// <summary>
/// One sold activation code. <paramref name="Days"/> is relative because a batch is priced by
/// duration; the absolute end date is only decided when somebody redeems it.
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
/// The activation-code ledger. Codes are written once by the operator and claimed exactly once by a
/// user, so the only concurrency that matters is "two clients redeem the same code at the same
/// time" — handled by claiming with a conditional UPDATE rather than by a transaction around the
/// read.
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

    /// <summary>True only for the first caller: the WHERE clause is what makes double redemption impossible.</summary>
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
    /// Marks a code dead. Returns false when the code is unknown or already redeemed: a sold code
    /// that paid out is the customer's property, only an unclaimed one can be pulled back.
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

    /// <summary>One row of the operator's ledger view; the buyer's address, not the user id.</summary>
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
    /// Codes newest first, for the operator who lost the batch the mint call returned. Only codes
    /// that were never printed for a buyer can be revoked, so the listing is what makes a mis-typed
    /// batch recoverable instead of wasted.
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
        // Spacing is written out instead of leaning on the raw strings: a raw literal drops the
        // newline before its closing quote, and "IS NULLORDER BY" is a syntax error, not a typo.
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
    /// The status filter, written once so List and Count can never disagree about what "open"
    /// means. The table is a few hundred rows, so the scan is not worth an index.
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
