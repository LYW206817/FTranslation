namespace FastTranslation.Server.Gateway;

/// <summary>
/// v210: 按账号限制同时在途的网关请求数。全站名额（<see cref="GatewayRequestCapacity"/>）
/// 只保证总量，不阻止单个账号把 16 个名额全占了；这条按账号的闸门让洪水账号
/// 最多占用自己那一小份，其余名额留给别人。
/// </summary>
public sealed class GatewayUserCapacity
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _active = [];

    /// <summary><paramref name="limit"/> 非正表示不限制（与限流器的约定一致）。</summary>
    public bool TryEnter(Guid userId, int limit)
    {
        if (limit <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            _active.TryGetValue(userId, out var current);
            if (current >= limit)
            {
                return false;
            }

            _active[userId] = current + 1;
            return true;
        }
    }

    public void Exit(Guid userId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(userId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                _active.Remove(userId);
            }
            else
            {
                _active[userId] = current - 1;
            }
        }
    }
}
