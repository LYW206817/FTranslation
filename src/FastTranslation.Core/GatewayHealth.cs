namespace FastTranslation.Core;

/// <summary>
/// 网关可用性的唯一真相源：只由后台探测写入，翻译路径只读结论。
/// v67: 原来用「现在时间 &lt; 冷却截止」判断，60 秒窗口一过，下一次翻译就当场去连一个
/// 没人监听的端口；这类连接要两秒才拿到 RST（过滤驱动/代理层），而它发生在 provider
/// 计时之外，用户看到「100 多毫秒」却实际等了三秒。改成"没探通就一直不走网关"后，
/// 用户路径上不再出现这种同步试探。
/// </summary>
public sealed class GatewayHealth
{
    /// <summary>判定不可用后，每隔这么久在后台重探一次；探通即恢复走网关。</summary>
    public const int DownProbeIntervalMs = 60_000;

    private int _usable;
    private int _probing;

    /// <summary>默认不可用：探测本身也要两秒，不能在启动后的头几秒把这两秒算进第一次翻译。</summary>
    public bool IsUsable => Volatile.Read(ref _usable) == 1;

    /// <summary>一次真实请求发现网关连不上或 5xx，立刻降级到用户自己的 Key。</summary>
    public void MarkDown() => Volatile.Write(ref _usable, 0);

    /// <summary>写入后台探测结论。</summary>
    public void MarkProbed(bool reachable) => Volatile.Write(ref _usable, reachable ? 1 : 0);

    /// <summary>同一时刻只允许一次探测，避免掉线时多个翻译各自发起探测。</summary>
    public bool TryBeginProbe() => Interlocked.CompareExchange(ref _probing, 1, 0) == 0;

    public void EndProbe() => Volatile.Write(ref _probing, 0);
}
