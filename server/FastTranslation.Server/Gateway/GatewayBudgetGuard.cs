using FastTranslation.Server.Storage;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 全站日预算的并发闸门。原实现是「读今天成本 → 放行 → 结束后记账」，一批并发请求
/// 会各自读到同一个旧成本全部放行，把 5 元日预算冲过数倍。
/// <para>
/// HTTP 发送前的预留与语音在途预留保存在内存，HTTP 发送后的占用由 SQLite 账本负责。
/// 每桶独立计算，待核对金额只占用发起当日的预算，跨日后仍保留供运营对账。
/// </para>
/// </summary>
public sealed class GatewayBudgetGuard(AccountStore store)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _reservedMicros = new(StringComparer.Ordinal);

    /// <summary>
    /// 尝试为一笔请求预留估算成本。已经花到预算上限时拒绝；否则允许「第一笔」
    /// 请求跨线（与旧的「花到上限才停」语义一致），后续并发请求按已占用金额判断。
    /// </summary>
    public bool TryReserve(string source, long budgetMicros, long estimateMicros)
    {
        if (budgetMicros <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            var (settled, pending, sending) = store.ReadTodayCosts(source);
            var used = settled + pending;
            _reservedMicros.TryGetValue(source, out var heldLocally);
            var held = heldLocally + sending;
            if (used >= budgetMicros)
            {
                return false;
            }

            // 第一笔允许越过预算线：预算很小（测试/演示）时仍能跑完一次，
            // 但同一时刻的后续请求会看到 held，不会跟着一起冲。
            var firstRequest = used == 0 && held == 0;
            if (!firstRequest && used + held + estimateMicros > budgetMicros)
            {
                return false;
            }

            _reservedMicros[source] = heldLocally + estimateMicros;
            return true;
        }
    }

    internal bool TransferReservation(string source, long estimateMicros, Func<bool> persist)
    {
        // 预算读取与转移共用锁，避免另一请求在持久化切换时漏计或重复看到同一笔。
        lock (_gate)
        {
            if (!persist()) return false;
            ReleaseCore(source, estimateMicros);
            return true;
        }
    }

    /// <summary>请求结束（成功记账、上游拒绝或异常）后释放预留。</summary>
    public void Release(string source, long estimateMicros)
    {
        lock (_gate)
        {
            ReleaseCore(source, estimateMicros);
        }
    }

    private void ReleaseCore(string source, long estimateMicros)
    {
        if (estimateMicros <= 0 || !_reservedMicros.TryGetValue(source, out var held)) return;
        var left = held - estimateMicros;
        if (left > 0) _reservedMicros[source] = left;
        else _reservedMicros.Remove(source);
    }
}
