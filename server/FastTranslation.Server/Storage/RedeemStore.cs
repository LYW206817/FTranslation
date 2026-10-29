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
    long? RedeemedAt)
{
    public bool IsRedeemed => RedeemedAt is not null;
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
            SELECT code, plan, days, note, created_at, redeemed_by, redeemed_at
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
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    /// <summary>True only for the first caller: the WHERE clause is what makes double redemption impossible.</summary>
    public bool TryClaim(string code, Guid userId, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE redeem_codes SET redeemed_by = $userId, redeemed_at = $now
            WHERE code = $code AND redeemed_at IS NULL
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$code", code);
        return command.ExecuteNonQuery() > 0;
    }

    public (int Total, int Redeemed) ReadStats()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(redeemed_at) FROM redeem_codes";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }
}
