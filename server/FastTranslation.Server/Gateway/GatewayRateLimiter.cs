using System.Collections.Concurrent;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// Per-account fixed-window limiter for the gateway. Kept in-process and tiny: the auth limiter is
/// per-IP because that is what protects a login form, but a translation flood comes from one
/// account wherever it roams, so this keys on the user id instead.
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

    /// <summary>Drops windows that have already rolled over so the dictionary cannot grow forever.</summary>
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
