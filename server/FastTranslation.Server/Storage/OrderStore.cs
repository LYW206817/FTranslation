using FastTranslation.Server.Domain;
using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

/// <summary>订单状态。字符串直接落库，端点与后台都按这些值判断。</summary>
public static class OrderStatus
{
    public const string PendingPayment = "pending_payment";
    public const string PendingReview = "pending_review";
    public const string Fulfilled = "fulfilled";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string status) =>
        status is PendingPayment or PendingReview or Fulfilled or Cancelled;

    public static bool IsActive(string status) =>
        status is PendingPayment or PendingReview;
}

public sealed record OrderRecord(
    Guid Id,
    Guid UserId,
    PlanTier Plan,
    string Cycle,
    int Days,
    int AmountCents,
    string Status,
    string PaymentMethod,
    string PaymentRef,
    long CreatedAt,
    long? SubmittedAt,
    long? ConfirmedAt,
    long? CancelledAt,
    string CancelReason);

/// <summary>后台订单列表的一行；带账号邮箱，运营不需要再查一次用户表。</summary>
public sealed record AdminOrderRow(
    Guid Id,
    string Email,
    PlanTier Plan,
    string Cycle,
    int Days,
    int AmountCents,
    string Status,
    string PaymentMethod,
    string PaymentRef,
    long CreatedAt,
    long? SubmittedAt,
    long? ConfirmedAt,
    long? CancelledAt,
    string CancelReason);

/// <summary>
/// 订阅订单账本。订单只在「确认到账」时改套餐，且订单状态与套餐延展在同一个
/// SQLite 事务里完成——这是这套流程唯一不能拆开的原子操作。
/// </summary>
public sealed class OrderStore(ServerDatabase database)
{
    /// <summary>
    /// 创建待支付订单。调用方虽然先查过 FindCurrent，但两台设备同时下单仍可能都通过检查；
    /// 活动订单唯一索引是最终防线，撞上就返回 null，由端点按 order_pending 处理。
    /// </summary>
    public OrderRecord? Create(
        Guid userId, PlanTier plan, string cycle, int days, int amountCents, long now)
    {
        var id = Guid.NewGuid();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO orders (id, user_id, plan, cycle, days, amount_cents, status, created_at)
            VALUES ($id, $userId, $plan, $cycle, $days, $amountCents, $status, $createdAt)
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$plan", (int)plan);
        command.Parameters.AddWithValue("$cycle", cycle);
        command.Parameters.AddWithValue("$days", days);
        command.Parameters.AddWithValue("$amountCents", amountCents);
        command.Parameters.AddWithValue("$status", OrderStatus.PendingPayment);
        command.Parameters.AddWithValue("$createdAt", now);
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            return null;
        }

        return new OrderRecord(
            id, userId, plan, cycle, days, amountCents, OrderStatus.PendingPayment,
            string.Empty, string.Empty, now, null, null, null, string.Empty);
    }

    public OrderRecord? Find(Guid id)
    {
        using var connection = database.Open();
        return Find(connection, null, id);
    }

    public OrderRecord? FindCurrent(Guid userId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, plan, cycle, days, amount_cents, status, payment_method,
                   payment_ref, created_at, submitted_at, confirmed_at, cancelled_at, cancel_reason
            FROM orders
            WHERE user_id = $userId AND status IN ($pendingPayment, $pendingReview)
            ORDER BY created_at DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$pendingPayment", OrderStatus.PendingPayment);
        command.Parameters.AddWithValue("$pendingReview", OrderStatus.PendingReview);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOrder(reader) : null;
    }

    public enum SubmitResult { Ok, NotPending, DuplicateRef }

    /// <summary>
    /// 提交交易单号。先去重再条件更新；唯一索引是最后一道防线，并发下后写的那笔
    /// 会撞 SQLITE_CONSTRAINT_UNIQUE，也按重复单号处理。
    /// </summary>
    public SubmitResult Submit(Guid id, Guid userId, string method, string paymentRef, long now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT 1 FROM orders WHERE payment_ref = $ref AND payment_ref <> '' LIMIT 1";
            duplicate.Parameters.AddWithValue("$ref", paymentRef);
            if (duplicate.ExecuteScalar() is not null)
            {
                transaction.Rollback();
                return SubmitResult.DuplicateRef;
            }
        }

        try
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders
                SET payment_method = $method, payment_ref = $ref,
                    status = $pendingReview, submitted_at = $now
                WHERE id = $id AND user_id = $userId AND status = $pendingPayment
                """;
            update.Parameters.AddWithValue("$method", method);
            update.Parameters.AddWithValue("$ref", paymentRef);
            update.Parameters.AddWithValue("$pendingReview", OrderStatus.PendingReview);
            update.Parameters.AddWithValue("$pendingPayment", OrderStatus.PendingPayment);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id.ToString("D"));
            update.Parameters.AddWithValue("$userId", userId.ToString("D"));
            if (update.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return SubmitResult.NotPending;
            }

            transaction.Commit();
            return SubmitResult.Ok;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            // SQLITE_CONSTRAINT_UNIQUE：另一个请求刚刚占用了同一个交易单号。
            transaction.Rollback();
            return SubmitResult.DuplicateRef;
        }
    }

    /// <summary>用户只能取消待支付；后台可取消待审核。返回 false 表示状态已经变了。</summary>
    public bool Cancel(Guid id, Guid? userId, bool allowReview, string reason, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var statuses = allowReview
            ? "$pendingPayment, $pendingReview"
            : "$pendingPayment";
        command.CommandText =
            "UPDATE orders SET status = $cancelled, cancelled_at = $now, cancel_reason = $reason " +
            "WHERE id = $id AND status IN (" + statuses + ")";
        if (userId is { } owner)
        {
            command.CommandText += " AND user_id = $userId";
            command.Parameters.AddWithValue("$userId", owner.ToString("D"));
        }

        command.Parameters.AddWithValue("$pendingPayment", OrderStatus.PendingPayment);
        if (allowReview) command.Parameters.AddWithValue("$pendingReview", OrderStatus.PendingReview);
        command.Parameters.AddWithValue("$cancelled", OrderStatus.Cancelled);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    public enum ConfirmResult { Ok, NotPending, PlanMismatch }

    /// <summary>
    /// 确认到账并开通。订单从 pending_review 条件更新成功后才延展套餐，两件事同一事务；
    /// 第二次点确认会返回 NotPending，不会重复加天数。
    /// 若运营在待审核期间已经手工改过套餐，这里必须拒绝：ExtendPlan 会把 users.plan
    /// 覆盖成订单档位，确认一张旧的低档订单等于把用户静默降档。
    /// </summary>
    public ConfirmResult Confirm(Guid id, long now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var order = Find(connection, transaction, id);
        if (order is null || order.Status != OrderStatus.PendingReview)
        {
            transaction.Rollback();
            return ConfirmResult.NotPending;
        }

        PlanTier currentPlan;
        long? expiresAt;
        using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT plan, plan_expires_at FROM users WHERE id = $id";
            owner.Parameters.AddWithValue("$id", order.UserId.ToString("D"));
            using var reader = owner.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return ConfirmResult.NotPending;
            }

            currentPlan = (PlanTier)reader.GetInt32(0);
            expiresAt = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        var active = Entitlement.EffectiveTier(currentPlan, expiresAt, now);
        if (active != PlanTier.Free && active != order.Plan)
        {
            transaction.Rollback();
            return ConfirmResult.PlanMismatch;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders SET status = $fulfilled, confirmed_at = $now
                WHERE id = $id AND status = $pendingReview
                """;
            update.Parameters.AddWithValue("$fulfilled", OrderStatus.Fulfilled);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id.ToString("D"));
            update.Parameters.AddWithValue("$pendingReview", OrderStatus.PendingReview);
            if (update.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return ConfirmResult.NotPending;
            }
        }

        var extended = AccountStore.ExtendPlan(
            connection, transaction, order.UserId, order.Plan, order.Days, now);
        if (!extended)
        {
            transaction.Rollback();
            return ConfirmResult.NotPending;
        }

        transaction.Commit();
        return ConfirmResult.Ok;
    }

    public (IReadOnlyList<AdminOrderRow> Rows, int Total) ListForAdmin(string status, int take, int skip)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? OrderStatus.PendingReview : status.Trim();
        var where = normalized == "all" ? string.Empty : " WHERE o.status = $status";
        using var connection = database.Open();

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM orders o" + where;
        if (where.Length > 0) count.Parameters.AddWithValue("$status", normalized);
        var total = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using var list = connection.CreateCommand();
        list.CommandText = """
            SELECT o.id, u.email, o.plan, o.cycle, o.days, o.amount_cents, o.status,
                   o.payment_method, o.payment_ref, o.created_at, o.submitted_at,
                   o.confirmed_at, o.cancelled_at, o.cancel_reason
            FROM orders o JOIN users u ON u.id = o.user_id
            """ + where + " ORDER BY o.created_at DESC LIMIT $take OFFSET $skip";
        if (where.Length > 0) list.Parameters.AddWithValue("$status", normalized);
        list.Parameters.AddWithValue("$take", take);
        list.Parameters.AddWithValue("$skip", skip);

        using var reader = list.ExecuteReader();
        var rows = new List<AdminOrderRow>();
        while (reader.Read())
        {
            rows.Add(new AdminOrderRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                (PlanTier)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.GetString(13)));
        }

        return (rows, total);
    }

    private static OrderRecord? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, user_id, plan, cycle, days, amount_cents, status, payment_method,
                   payment_ref, created_at, submitted_at, confirmed_at, cancelled_at, cancel_reason
            FROM orders WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOrder(reader) : null;
    }

    private static OrderRecord ReadOrder(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        (PlanTier)reader.GetInt32(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),
        reader.IsDBNull(12) ? null : reader.GetInt64(12),
        reader.GetString(13));
}
