using System.Net.WebSockets;

namespace FastTranslation.Core.Speech;

/// <summary>语音中继下行的四种事件,对应设计 §6.1 的 ready/sentence/usage/error 帧。</summary>
public enum SpeechTransportKind { Ready, Sentence, Usage, Error }

/// <summary>一次下行事件;按 Kind 取用相应字段,其余保持默认值。</summary>
public sealed record SpeechTransportEvent(
    SpeechTransportKind Kind,
    int SentenceId = 0,
    string Text = "",
    bool IsFinal = false,
    double Seconds = 0,
    string Code = "",
    string Message = "");

/// <summary>
/// 语音会话的传输抽象。接口签名不出现 WebSocket 类型,把「帧怎么说」留给实现;
/// 断连语义用异常表达(发送失败即断线),由协调器统一处理重连。
/// </summary>
public interface ISpeechTransport : IAsyncDisposable
{
    /// <summary>建立连接并发送 start 帧;连接被拒(未登录/额度/停用)以异常结束。</summary>
    Task ConnectAsync(SpeechSessionOptions options, CancellationToken ct = default);

    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default);

    /// <summary>发送 stop 帧并等待服务端收尾;连接可能已经断开,实现必须吞掉这类失败。</summary>
    Task StopAsync(CancellationToken ct = default);

    event Action<SpeechTransportEvent>? Received;
}
