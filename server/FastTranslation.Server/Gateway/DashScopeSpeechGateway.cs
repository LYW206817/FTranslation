using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;

namespace FastTranslation.Server.Gateway;

/// <summary>一次语音会话的记账结果。Recorded=false 表示上游从未报过用量(比如纯静音),不记账。</summary>
public sealed record SpeechRelayResult(bool Recorded, long QuotaChars, long CostMicros, double? DurationSeconds);

/// <summary>
/// 语音中继:客户端只认 5 种帧(设计 §6.1),DashScope 的 run-task 协议全部留在这里,
/// 以后换讯飞/腾讯只改这一个类。上游用量只出现在 result-generated 帧上(会话累计,
/// task-finished 帧没有——POC 实证),所以客户端半路断掉也要把见过的用量记账。
/// </summary>
public sealed class DashScopeSpeechGateway
{
    /// <summary>DashScope 一帧 result-generated 里报的用量(会话累计)。</summary>
    private readonly record struct AsrUsage(int DurationSeconds, long InputTokens, long OutputTokens);

    private readonly SpeechOptions options;
    private readonly string apiKey;
    private readonly Func<CancellationToken, Task<WebSocket>> connectUpstream;
    private string runTaskId = string.Empty;

    public DashScopeSpeechGateway(SpeechOptions options, string apiKey,
        Func<CancellationToken, Task<WebSocket>>? connectUpstream = null)
    {
        this.options = options;
        this.apiKey = apiKey;
        this.connectUpstream = connectUpstream ?? ConnectDashScopeAsync;
    }

    /// <summary>默认上游:DashScope WebSocket。Key 是网关自己的,永远不发给客户端。</summary>
    private async Task<WebSocket> ConnectDashScopeAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        if (options.WorkspaceId.Length > 0)
        {
            ws.Options.SetRequestHeader("X-DashScope-Workspace", options.WorkspaceId);
        }
        await ws.ConnectAsync(new Uri(options.WsUrl), ct);
        return ws;
    }

    public async Task<SpeechRelayResult> RelayAsync(WebSocket client, Guid userId, Guid deviceId,
        AccountStore store, ServerDatabase database, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        // 会话硬顶:到点我们自己断,防止客户端消失后上游连接挂着烧钱(设计 §6.1)。
        timeout.CancelAfter(TimeSpan.FromSeconds(options.MaxSecondsPerSession));

        WebSocket? upstream = null;
        Task upstreamPump = Task.CompletedTask;
        var finishSent = false;
        AsrUsage? latestUsage = null;
        var buffer = new byte[64 * 1024];
        var result = new SpeechRelayResult(false, 0, 0, null);

        try
        {
            while (client.State == WebSocketState.Open && !timeout.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (received.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
                    if (TryParseStart(text, out var sampleRate, out var language) && upstream is null)
                    {
                        upstream = await connectUpstream(timeout.Token);
                        await SendRunTaskAsync(upstream, sampleRate, language, timeout.Token);
                        upstreamPump = PumpUpstreamAsync(client, upstream, timeout.Token,
                            usage => latestUsage = usage);
                    }
                    else if (IsStop(text) && upstream is not null)
                    {
                        finishSent = true;
                        await SendFinishTaskAsync(upstream, timeout.Token);
                        // stop 之后客户端的戏份完了:usage/关流都由上游泵收尾。
                        break;
                    }
                }
                else if (upstream is not null && received.Count > 0)
                {
                    await upstream.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count),
                        WebSocketMessageType.Binary, received.EndOfMessage, timeout.Token);
                }
            }
        }
        catch (WebSocketException)
        {
            // 客户端半路断开是常态(用户直接关页面);requestAborted 会连带取消 linked CTS,
            // 所以这里不能再挂 when 守卫,否则正常断连会漏成 500。finally 里统一收尾记账。
        }
        catch (OperationCanceledException)
        {
            // 客户端中止或会话硬顶到期:finally 里照常收尾、记账。
        }
        finally
        {
            if (upstream is not null)
            {
                // 客户端没说 stop 也必须给上游发 finish-task,否则上游挂住:末段丢失、用量不准、连接烧钱。
                if (!finishSent)
                {
                    try
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await SendFinishTaskAsync(upstream, grace.Token);
                    }
                    catch (Exception)
                    {
                        // 上游已经死了就算了:见过的 usage 已在泵里记下,没见过就当纯静音不记账。
                    }
                }
                // 给上游泵最多 2 秒,把 task-finished 之前最后的 usage 冲出来。
                try { await upstreamPump.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception)
                {
                    // 泵超时或失败都不影响记账:记账只看已收到的 usage。
                }
                upstream.Dispose();
            }

            if (latestUsage is { } usage)
            {
                // 额度是产品语言(按秒折字符),成本是财务语言(按 token 算钱),分开记(设计 §5.5)。
                var seconds = Math.Max(usage.DurationSeconds, options.MinSeconds);
                var quotaChars = (long)seconds * options.QuotaCharsPerSecond;
                var cost = Pricing.TextCostMicros(options.Model, usage.InputTokens, usage.OutputTokens);
                store.RecordUsage(userId, deviceId, (int)Math.Min(quotaChars, int.MaxValue), options.Model,
                    (int)Math.Min(usage.InputTokens, int.MaxValue), (int)Math.Min(usage.OutputTokens, int.MaxValue),
                    cost, database.NowUnix, source: "speech");
                result = new SpeechRelayResult(true, quotaChars, cost, usage.DurationSeconds);
            }
        }
        return result;
    }

    /// <summary>上游泵:把 DashScope 的帧翻译成客户端的 5 种帧。只在终稿前转 sentence,part 永远不出门。</summary>
    private async Task PumpUpstreamAsync(WebSocket client, WebSocket upstream, CancellationToken ct,
        Action<AsrUsage> onUsage)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (upstream.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var received = await upstream.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
                if (!json.RootElement.TryGetProperty("header", out var header)
                    || !header.TryGetProperty("event", out var evt))
                {
                    continue;
                }
                switch (evt.GetString())
                {
                    case "task-started":
                        await SendTextAsync(client, "{\"type\":\"ready\"}", ct);
                        break;
                    case "result-generated":
                        if (TryReadSentence(json.RootElement, out var sentenceId, out var sentenceText, out var final))
                        {
                            var frame = "{\"type\":\"sentence\",\"id\":" + sentenceId.ToString(CultureInfo.InvariantCulture)
                                + ",\"text\":" + JsonSerializer.Serialize(sentenceText)
                                + ",\"final\":" + (final ? "true" : "false") + "}";
                            await SendTextAsync(client, frame, ct);
                        }
                        if (TryReadUsage(json.RootElement, out var usage))
                        {
                            onUsage(usage);
                            var usageFrame = "{\"type\":\"usage\",\"seconds\":"
                                + usage.DurationSeconds.ToString(CultureInfo.InvariantCulture) + "}";
                            await SendTextAsync(client, usageFrame, ct);
                        }
                        break;
                    case "task-finished":
                        return;
                    case "task-failed":
                        var code = header.TryGetProperty("error_code", out var errorCode)
                            ? errorCode.GetString() ?? "task_failed" : "task_failed";
                        var message = header.TryGetProperty("error_message", out var errorMessage)
                            ? errorMessage.GetString() ?? "语音识别失败" : "语音识别失败";
                        await SendTextAsync(client,
                            JsonSerializer.Serialize(new { type = "error", code, message }), ct);
                        return;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 硬顶到期/请求中止:正常退出,调用方 finally 收尾。
        }
        finally
        {
            // 上游泵结束后给客户端一个正常关闭,它可能还在等帧。
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "finished", CancellationToken.None);
                }
                catch (Exception)
                {
                    // 客户端可能已经自己断了。
                }
            }
        }
    }

    private async Task SendRunTaskAsync(WebSocket upstream, int sampleRate, string language, CancellationToken ct)
    {
        runTaskId = Guid.NewGuid().ToString("D");
        // language=auto 时不带 language_hints(DashScope 会自己判断);POC 只实证过显式语种,
        // auto 的行为留到真实联调确认(残留风险已记 IMPL)。
        var hints = string.Equals(language, "auto", StringComparison.Ordinal)
            ? string.Empty
            : ",\"language_hints\":[\"" + language + "\"]";
        var frame = "{\"header\":{\"action\":\"run-task\",\"task_id\":\"" + runTaskId
            + "\",\"streaming\":\"duplex\"},\"payload\":{\"task_group\":\"audio\",\"task\":\"asr\","
            + "\"function\":\"recognition\",\"model\":\"" + options.Model
            + "\",\"parameters\":{\"format\":\"pcm\",\"sample_rate\":"
            + sampleRate.ToString(CultureInfo.InvariantCulture) + hints + "},\"input\":{}}}";
        await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame)),
            WebSocketMessageType.Text, true, ct);
    }

    private async Task SendFinishTaskAsync(WebSocket upstream, CancellationToken ct)
    {
        var frame = "{\"header\":{\"action\":\"finish-task\",\"task_id\":\"" + runTaskId
            + "\",\"streaming\":\"duplex\"},\"payload\":{\"input\":{}}}";
        await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame)),
            WebSocketMessageType.Text, true, ct);
    }

    private static async Task SendTextAsync(WebSocket client, string text, CancellationToken ct)
    {
        await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text, true, ct);
    }

    private static bool TryParseStart(string text, out int sampleRate, out string language)
    {
        sampleRate = 16000;
        language = "auto";
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("type", out var type)
                || type.GetString() != "start")
            {
                return false;
            }
            if (json.RootElement.TryGetProperty("sampleRate", out var rate)
                && rate.ValueKind == JsonValueKind.Number)
            {
                sampleRate = rate.GetInt32();
            }
            if (json.RootElement.TryGetProperty("language", out var lang)
                && lang.ValueKind == JsonValueKind.String)
            {
                language = lang.GetString() ?? "auto";
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStop(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == "stop";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSentence(JsonElement root, out int sentenceId, out string text, out bool final)
    {
        sentenceId = 0;
        text = string.Empty;
        final = false;
        // sentence_id/end_time 在部分帧上可以是 null(POC 崩过一次),全部防御式读。
        if (!root.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("output", out var output)
            || !output.TryGetProperty("sentence", out var sentence)
            || sentence.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (sentence.TryGetProperty("sentence_id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            sentenceId = id.GetInt32();
        }
        if (sentence.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            text = t.GetString() ?? string.Empty;
        }
        if (sentence.TryGetProperty("sentence_end", out var end) && end.ValueKind == JsonValueKind.True)
        {
            final = true;
        }
        return true;
    }

    private static bool TryReadUsage(JsonElement root, out AsrUsage usage)
    {
        usage = default;
        if (!root.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("usage", out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var seconds = raw.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32() : 0;
        var tokensIn = raw.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number
            ? i.GetInt64() : 0;
        var tokensOut = raw.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt64() : 0;
        usage = new AsrUsage(seconds, tokensIn, tokensOut);
        return true;
    }
}