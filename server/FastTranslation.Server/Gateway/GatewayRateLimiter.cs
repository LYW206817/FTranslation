using System.Collections.Concurrent;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 网关侧按账号的固定窗口限流器。保持进程内、体量极小：认证限流器按
/// IP 是因为登录表单需要以此防护，而翻译洪水来自同一个
/// 账号，无论它走到哪个 IP，所以这里改以用户 id 为键。
/// </summary>
public sealed class GatewayRateLimiter(TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<Guid, Window> _windows = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool TryAcquire(Guid userId, int permitPerMinute)
    {
        if (permitPerMinute <= 0)
        {
            return true;
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var window = _windows.AddOrUpdate(
            userId,
            _ => new Window(now - (now % 60) + 60, 1),
            (_, existing) => existing.ResetAt > now ? existing with { Count = existing.Count + 1 } : new Window(now - (now % 60) + 60, 1));

        return window.Count <= permitPerMinute;
    }

    /// <summary>清除已经滚过的窗口，避免字典无限增长。</summary>
    public void Prune()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var pair in _windows)
        {
            if (pair.Value.ResetAt <= now)
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private readonly record struct Window(long ResetAt, int Count);
}
