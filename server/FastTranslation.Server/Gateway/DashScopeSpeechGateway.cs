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

    /// <param name="sessionSeconds">本次会话允许的最长秒数(已按剩余额度截断,见 SpeechQuota)。</param>
    public async Task<SpeechRelayResult> RelayAsync(WebSocket client, Guid userId, Guid deviceId,
        AccountStore store, ServerDatabase database, int sessionSeconds, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        // 会话硬顶:到点我们自己断,防止客户端消失后上游连接挂着烧钱(设计 §6.1)。
        // 秒数由调用方按剩余额度算好——讲满 30 分钟不等于有 30 分钟的额度(v81)。
        timeout.CancelAfter(TimeSpan.FromSeconds(sessionSeconds));

        WebSocket? upstream = null;
        Task upstreamPump = Task.CompletedTask;
        var finishSent = false;
        AsrUsage? latestUsage = null;
        var buffer = new byte[64 * 1024];
        var result = new SpeechRelayResult(false, 0, 0, null);
        var startedAtUtc = DateTimeOffset.UtcNow;
        int billedSeconds = 0;
        long billedIn = 0, billedOut = 0, billedChars = 0, billedCost = 0;

        // 上游用量是「会话累计」值,每来一帧就按增量落一行 usage_events:
        // 这样日预算/额度闸门看到的是实时花费,客户端半路跑掉也不会丢账(v81)。
        void Bill(int durationSeconds, long tokensIn, long tokensOut)
        {
            var seconds = Math.Max(durationSeconds, billedSeconds);
            var deltaSeconds = seconds - billedSeconds;
            var deltaIn = Math.Max(0L, tokensIn - billedIn);
            var deltaOut = Math.Max(0L, tokensOut - billedOut);
            if (deltaSeconds == 0 && deltaIn == 0 && deltaOut == 0)
            {
                return;
            }

            var chars = (long)deltaSeconds * options.QuotaCharsPerSecond;
            var cost = Pricing.TextCostMicros(options.Model, deltaIn, deltaOut);
            store.RecordUsage(userId, deviceId, (int)Math.Min(chars, int.MaxValue), options.Model,
                (int)Math.Min(deltaIn, int.MaxValue), (int)Math.Min(deltaOut, int.MaxValue),
                cost, database.NowUnix, source: "speech");
            billedSeconds = seconds;
            billedIn = tokensIn;
            billedOut = tokensOut;
            billedChars += chars;
            billedCost += cost;
        }

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
                        upstreamPump = PumpUpstreamAsync(client, upstream, timeout.Token, usage =>
                        {
                            latestUsage = usage;
                            Bill(usage.DurationSeconds, usage.InputTokens, usage.OutputTokens);
                        });
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
            // 会话硬顶到期(不是客户端自己走)必须告诉用户为什么停:否则客户端只看到连接断,
            // 会显示成「语音连接中断」,用户以为是网络问题(v81)。
            if (timeout.IsCancellationRequested && !requestAborted.IsCancellationRequested)
            {
                await TrySendErrorAsync(client, "session_timeout",
                    "本次语音已达时长上限(按剩余额度计算),请稍后重新开始").ConfigureAwait(false);
            }
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
                // 补足最短计费秒数(讲半秒也按 MinSeconds 记);增量部分已在每帧记过,不会重复。
                // 额度是产品语言(按秒折字符),成本是财务语言(按 token 算钱),分开记(设计 §5.5)。
                Bill(Math.Max(usage.DurationSeconds, options.MinSeconds), usage.InputTokens, usage.OutputTokens);
                result = new SpeechRelayResult(billedChars > 0, billedChars, billedCost, usage.DurationSeconds);
            }
            else
            {
                // 一条 usage 都没收到(连上就断、全程静音、上游早死):按会话墙钟计费。
                // 不这么做的话「连上就砍」可以白嫖上游 ASR 算力——预检已经放行,不能不计账(v81)。
                var wall = Math.Min(Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds),
                    sessionSeconds);
                Bill(Math.Max(wall, options.MinSeconds), 0, 0);
                result = new SpeechRelayResult(billedChars > 0, billedChars, billedCost, wall);
            }

            // 关流只在这里做:上游泵不再自己关客户端,否则「硬顶到期的错误帧」会和关闭抢时序,
            // 客户端有时只看到连接断了、不知道是额度讲完了(v81)。
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
        return result;
    }

    private static async Task TrySendErrorAsync(WebSocket client, string code, string message)
    {
        if (client.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var json = "{\"type\":\"error\",\"code\":\"" + code + "\",\"message\":\"" + message + "\"}";
            await client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 尽力而为:客户端已经走了就没什么可通知的,收尾记账不受影响
        }
    }

    /// <summary>上游泵:把 DashScope 的帧翻译成客户端的 5 种帧。只在终稿前转 sentence,part 永远不出门。</summary>
    private async Task PumpUpstreamAsync(WebSocket client, WebSocket upstream, CancellationToken ct,
        Action<AsrUsage> onUsage)
    {
        var buffer = new byte[64 * 1024];
        // 一条 JSON 可能跨多帧(DashScope 的长句终稿带 words[],单帧可达数十 KB);
        // 必须等 EndOfMessage 拼完再解析,否则截断的 JSON 会被当成坏帧静默丢掉(v81)。
        using var textBuffer = new MemoryStream();
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
                textBuffer.Write(buffer, 0, received.Count);
                while (!received.EndOfMessage)
                {
                    received = await upstream.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (received.MessageType != WebSocketMessageType.Text)
                    {
                        break;
                    }

                    textBuffer.Write(buffer, 0, received.Count);
                }

                using var json = JsonDocument.Parse(textBuffer.GetBuffer().AsMemory(0, (int)textBuffer.Length));
                textBuffer.SetLength(0);
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