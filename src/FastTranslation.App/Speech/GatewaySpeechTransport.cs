using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using FastTranslation.Core.Speech;

namespace FastTranslation.App.Speech;

/// <summary>
/// 对自建网关 /v1/asr 的 WebSocket 传输,只说设计 §6.1 的 5 种帧。
/// DashScope 的头与 payload 全留在服务端,这里换供应商不用动。
/// </summary>
public sealed class GatewaySpeechTransport : ISpeechTransport
{
    private readonly Func<Task<string?>> _accessToken;
    private readonly Uri _gatewayBaseUri;
    private readonly string _deviceFingerprint;
    private readonly HttpClient? _sharedClient;
    private readonly CancellationTokenSource _closed = new();
    private ClientWebSocket? _socket;

    /// <param name="sharedClient">可选的共享 HttpClient(应用就一个);传了就不在每次会话里新建。
    /// 复用是为了不再每条会话漏一个连接池对象——预检是每次开始的固定动作(v81)。</param>
    public GatewaySpeechTransport(
        Func<Task<string?>> accessToken, Uri gatewayBaseUri, string deviceFingerprint, HttpClient? sharedClient = null)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentNullException.ThrowIfNull(gatewayBaseUri);
        _accessToken = accessToken;
        _gatewayBaseUri = gatewayBaseUri;
        _deviceFingerprint = deviceFingerprint;
        _sharedClient = sharedClient;
    }

    public event Action<SpeechTransportEvent>? Received;

    public async Task ConnectAsync(SpeechSessionOptions options, CancellationToken ct = default)
    {
        var token = await _accessToken().ConfigureAwait(false)
            ?? throw new InvalidOperationException("speech requires sign-in");
        // 升级前先发一次普通 GET:网关把 401/403/429/503 以普通 HTTP 状态给出,
        // 400(非 WS 请求)才是闸门全过的绿灯——额度类失败在这里就能拿到明确文案,
        // 不用等 WS 握手莫名其妙地失败
        var asr = AsrUri();
        var probeUri = new UriBuilder(asr) { Scheme = asr.Scheme == "wss" ? "https" : "http" }.Uri;
        // 预检自己限时 10 秒:共享 HttpClient 的 Timeout 是 45 秒,不能让它拖着开始会话
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var probeClient = _sharedClient ?? new HttpClient();
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, probeUri);
            probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            probe.Headers.Add("X-FT-Device", _deviceFingerprint);
            using var probeResponse = await probeClient.SendAsync(probe, probeTimeout.Token).ConfigureAwait(false);
            if (probeResponse.StatusCode != HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException(await DescribeRefusalAsync(probeResponse).ConfigureAwait(false));
            }
        }
        finally
        {
            if (_sharedClient is null)
            {
                probeClient.Dispose();
            }
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.SetRequestHeader("X-FT-Device", _deviceFingerprint);
        await socket.ConnectAsync(AsrUri(), ct).ConfigureAwait(false);
        _socket = socket;
        _ = PumpAsync(socket);
        // 只报采样率:识别语种交给 ASR 自动判断。把「译成哪种语言」当 language_hints 发过去,
        // 会让英文音频被硬按中文识别——曾经就是这样错的(v81 修)。
        var start = "{\"type\":\"start\",\"sampleRate\":" + options.SourceRate.ToString(CultureInfo.InvariantCulture) + "}";
        await SendTextAsync(socket, start, ct).ConfigureAwait(false);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("speech transport is not connected");
        await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null)
            return;
        try
        {
            await SendTextAsync(socket, "{\"type\":\"stop\"}", ct).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or InvalidOperationException)
        {
            // 服务端往往抢先关连接;收尾失败不再重试。
        }
        finally
        {
            _closed.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed.Cancel();
        _socket?.Abort();
        _socket?.Dispose();
        _closed.Dispose();
        return ValueTask.CompletedTask;
    }

    private Uri AsrUri()
    {
        var scheme = _gatewayBaseUri.Scheme == "https" ? "wss" : "ws";
        // v94: 传进来的 base 已经带 /v1（App 的 GatewayBaseUrl() = host + "/v1"，与 /v1/image、
        // /v1/ocr 同口径），这里再拼一次 /v1 会打出不存在的 /v1/v1/asr：预检拿到 404 而不是
        // 闸门全过的 400，会话在连接阶段就被判死。服务端注册的是 app.Map("/v1/asr")。
        var path = _gatewayBaseUri.AbsolutePath.TrimEnd('/') + "/asr";
        return new UriBuilder(_gatewayBaseUri) { Scheme = scheme, Path = path }.Uri;
    }

    // 把网关在升级前给的状态码翻译成用户能懂的话;400 之外都视为拒绝
    private static async Task<string> DescribeRefusalAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return "语音服务暂未开启";
        if (response.StatusCode == HttpStatusCode.Forbidden) return "账号状态异常,请重新登录";
        if (response.StatusCode == HttpStatusCode.Unauthorized) return "登录已过期,请重新登录";
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // v106: 网关在 429 上带 X-FT-Quota-Reason，区分月度/日额度；以前只判断
            // 「有没有这个头」就统一说「下月再来」，日额度耗尽的人会白等一个月。
            if (response.Headers.TryGetValues("X-FT-Quota-Reason", out var reasons))
            {
                return SpeechQuotaGate.DescribeQuotaReason(reasons.FirstOrDefault());
            }
            if (body.Contains("speech_busy", StringComparison.Ordinal)) return "已有语音会话在进行,请稍后再试";
            if (body.Contains("budget_exhausted", StringComparison.Ordinal)) return "今日语音服务繁忙,明天再试";
            return "语音请求太频繁,请稍后再试";
        }
        return "无法连接语音服务(HTTP " + (int)response.StatusCode + ")";
    }

    private async Task PumpAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _closed.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text
                    && TryParseEvent(Encoding.UTF8.GetString(message.ToArray()), out var e))
                {
                    Received?.Invoke(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            // 服务端断开:协调器在下一拍发送时感知并走重连。
        }
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static bool TryParseEvent(string json, out SpeechTransportEvent e)
    {
        e = new SpeechTransportEvent(SpeechTransportKind.Error);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }

            switch (type.GetString())
            {
                case "ready":
                    e = new SpeechTransportEvent(SpeechTransportKind.Ready);
                    return true;
                case "sentence":
                {
                    var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? idEl.GetInt32() : 0;
                    var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? textEl.GetString() ?? "" : "";
                    var final = root.TryGetProperty("final", out var finalEl) && finalEl.ValueKind == System.Text.Json.JsonValueKind.True;
                    e = new SpeechTransportEvent(SpeechTransportKind.Sentence, id, text, final);
                    return true;
                }
                case "usage":
                {
                    var seconds = root.TryGetProperty("seconds", out var secondsEl) && secondsEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? secondsEl.GetDouble() : 0;
                    e = new SpeechTransportEvent(SpeechTransportKind.Usage, Seconds: seconds);
                    return true;
                }
                case "error":
                {
                    var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? codeEl.GetString() ?? "" : "";
                    var text = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? messageEl.GetString() ?? "" : "";
                    e = new SpeechTransportEvent(SpeechTransportKind.Error, Code: code, Message: text);
                    return true;
                }
                default:
                    return false;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
