namespace FastTranslation.Server.Gateway;

/// <summary>限制同时读包、解析及等待上游的 HTTP 网关请求数量。</summary>
internal sealed class GatewayRequestCapacity(GatewayOptions options)
{
    private readonly int _limit = Math.Max(1, options.MaxConcurrentRequests);
    private int _active;

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _active) <= _limit)
        {
            return true;
        }
        Exit();
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _active);
}
