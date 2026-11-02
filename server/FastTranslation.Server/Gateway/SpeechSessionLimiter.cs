using System.Collections.Concurrent;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 按账号统计正在进行的语音会话数。
/// 限流器只数「每分钟请求数」,拦不住同一账号一分钟内开几十条 30 分钟的会话——
/// 每条都在启动时只预检最小额度,那是真实的成本放大器,所以单独加一道并发闸门(v81)。
/// </summary>
public sealed class SpeechSessionLimiter
{
    private readonly ConcurrentDictionary<Guid, int> _active = new();

    /// <summary>尝试占一个并发位。<paramref name="max"/> ≤ 0 表示不限制。</summary>
    public bool TryEnter(Guid userId, int max)
    {
        if (max <= 0)
        {
            return true;
        }

        while (true)
        {
            _active.TryGetValue(userId, out var current);
            if (current >= max)
            {
                return false;
            }

            if (current == 0)
            {
                if (_active.TryAdd(userId, 1))
                {
                    return true;
                }
            }
            else if (_active.TryUpdate(userId, current + 1, current))
            {
                return true;
            }
        }
    }

    /// <summary>释放一个并发位。重复释放是安全的。</summary>
    public void Exit(Guid userId)
    {
        while (true)
        {
            if (!_active.TryGetValue(userId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                if (_active.TryRemove(new KeyValuePair<Guid, int>(userId, current)))
                {
                    return;
                }
            }
            else if (_active.TryUpdate(userId, current - 1, current))
            {
                return;
            }
        }
    }
}
