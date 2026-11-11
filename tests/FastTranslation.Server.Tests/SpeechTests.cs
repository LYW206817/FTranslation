using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>开语音、用假上游替换 DashScope 连接的测试工厂。</summary>
public class SpeechGatewayFactory : GatewayFactory
{
    /// <summary>禁用语音的单测把它关掉,验证 503 speech_disabled 而不是 400。</summary>
    protected virtual bool SpeechEnabled => true;

    /// <summary>额度测试把它调大,让一次预检就超月配额,不必真烧 5 万字符。</summary>
    protected virtual int QuotaCharsPerSecond => 20;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:Gateway:Speech:Enabled"] = SpeechEnabled ? "true" : "false",
            ["Server:Gateway:Speech:WsUrl"] = "ws://speech.test/asr",
            ["Server:Gateway:Speech:Model"] = "qwen-audio-3.1-asr-flash-streaming",
            ["Server:Gateway:Speech:QuotaCharsPerSecond"] = QuotaCharsPerSecond.ToString(),
            ["Server:Gateway:Speech:DailyBudgetMicros"] = "2000000",
        }));
    }
}

public sealed class HugeQuotaSpeechFactory : SpeechGatewayFactory
{
    // Free 日配额 2,000、Light 月配额 50,000;10 秒 × 60,000 = 600,000,一次预检就爆。
    protected override int QuotaCharsPerSecond => 60_000;
}

public sealed class DisabledSpeechFactory : SpeechGatewayFactory
{
    protected override bool SpeechEnabled => false;
}

/// <summary>默认配置的契约：生产不显式覆盖时必须按 50 字符/秒记，客户端镜像同值。</summary>
public sealed class SpeechOptionsTests
{
    [Fact]
    public void Default_quota_rate_is_fifty_chars_per_second() =>
        Assert.Equal(50, new SpeechOptions().QuotaCharsPerSecond);
}

public class SpeechEndpointTests : IClassFixture<SpeechGatewayFactory>, IClassFixture<HugeQuotaSpeechFactory>
{
    private readonly SpeechGatewayFactory _factory;
    private readonly HugeQuotaSpeechFactory _hugeQuota;

    public SpeechEndpointTests(SpeechGatewayFactory factory, HugeQuotaSpeechFactory hugeQuota)
    {
        _factory = factory;
        _hugeQuota = hugeQuota;
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, PlanTier plan)
    {
        var client = factory.CreateClient();
        var email = $"speech-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free)
        {
            using var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
            {
                Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), null)),
            };
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }
        return client;
    }

    [Fact]
    public async Task Asr_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/v1/asr");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Asr_returns_503_when_speech_disabled()
    {
        using var disabled = new DisabledSpeechFactory();
        var client = await SignedInAsync(disabled, PlanTier.Free);

        var response = await client.GetAsync("/v1/asr");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("speech_disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asr_refuses_when_quota_is_spent()
    {
        // 预检按最小会话(MinSeconds×QuotaCharsPerSecond)折算,600,000 字符直接超过月配额。
        var client = await SignedInAsync(_hugeQuota, PlanTier.Free);

        var response = await client.GetAsync("/v1/asr");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var reason = Assert.Single(response.Headers.GetValues("X-FT-Quota-Reason"));
        Assert.Contains("quota_exhausted", reason);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asr_admits_a_session_when_only_the_shortest_billable_is_left()
    {
        // v98: 最短计费 10 秒 → 5 秒,预检门槛从 200 字符降到 100 字符。
        // 免费档日额度 2,000 先烧掉 1,850 只剩 150:按旧的 200 门槛必须 429,按 5 秒门槛必须放行。
        // 闸门全过但请求没带 WS 升级头,端点收在 400——用 400/429 之差断言「预检放行了」。
        var client = await SignedInAsync(_factory, PlanTier.Free);
        var burn = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = new string('a', 1_850) } },
            translation_options = new { source_lang = "en", target_lang = "zh" },
        });
        burn.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/v1/asr");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Asr_returns_429_when_the_account_already_has_a_session()
    {
        // 并发闸门是单例,测试可以先替这个账号占住唯一的名额,再打端点看它 429。
        var client = _factory.CreateClient();
        var email = $"speech-busy-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var store = _factory.Services.GetRequiredService<AccountStore>();
        var user = store.FindUserByEmail(email)!;
        var sessions = _factory.Services.GetRequiredService<SpeechSessionLimiter>();
        Assert.True(sessions.TryEnter(user.Id, 1));
        try
        {
            var busy = await client.GetAsync("/v1/asr");
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Contains("speech_busy", await busy.Content.ReadAsStringAsync());
        }
        finally
        {
            sessions.Exit(user.Id);
        }

        // 名额放回去之后,同一账号能过闸门(非 WS 请求收在 400)。
        var released = await client.GetAsync("/v1/asr");
        Assert.Equal(HttpStatusCode.BadRequest, released.StatusCode);
    }
}

/// <summary>测试侧的假 socket 收发帮手;收到非文本帧直接失败。</summary>
internal static class SpeechSocketExtensions
{
    public static Task SendTextAsync(this WebSocket socket, string text)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Timeout());

    public static Task SendBinaryAsync(this WebSocket socket, byte[] payload)
        => socket.SendAsync(payload, WebSocketMessageType.Binary, true, Timeout());

    /// <summary>发一条被拆开的文本帧片段(EndOfMessage=false 表示后面还有)。</summary>
    public static Task SendTextFragmentAsync(this WebSocket socket, string text, bool endOfMessage)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage, Timeout());

    public static async Task<string> ReceiveTextAsync(this WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(buffer, Timeout());
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private static CancellationToken Timeout()
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return source.Token;
    }
}

/// <summary>内存里的 WebSocket 假对端:SendAsync 把帧塞进对端的接收队列,ReceiveAsync 读自己的。
/// RelayAsync 的两个泵只依赖 WebSocket 抽象,所以真客户端可以换成这一对;Abort 会把对端的
/// 接收队列一起收掉,模拟真实的连接骤断。</summary>
public sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage)> incoming =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
    private FakeWebSocket peer = null!;
    private WebSocketState state = WebSocketState.Open;

    private FakeWebSocket()
    {
    }

    public static (FakeWebSocket TestSide, FakeWebSocket RelaySide) CreatePair()
    {
        var testSide = new FakeWebSocket();
        var relaySide = new FakeWebSocket();
        testSide.peer = relaySide;
        relaySide.peer = testSide;
        return (testSide, relaySide);
    }

    public override WebSocketState State => state;

    public override string? SubProtocol => null;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (state != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.InvalidState);
        }
        var copy = new byte[buffer.Count];
        Array.Copy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
        try
        {
            await peer.incoming.Writer.WriteAsync((copy, messageType, endOfMessage), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new WebSocketException(WebSocketError.InvalidState);
        }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        (byte[] Payload, WebSocketMessageType Type, bool EndOfMessage) frame;
        try
        {
            frame = await incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // 对端 Abort 掉了:模拟底层连接骤断,让泵收到和真 WebSocket 一样的异常。
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }
        if (frame.Type == WebSocketMessageType.Close)
        {
            state = WebSocketState.CloseReceived;
        }
        var count = Math.Min(frame.Payload.Length, buffer.Count);
        if (count > 0)
        {
            Array.Copy(frame.Payload, buffer.Array!, count);
        }
        return new WebSocketReceiveResult(count, frame.Type, frame.EndOfMessage);
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        if (state != WebSocketState.Open)
        {
            return;
        }
        state = WebSocketState.CloseSent;
        await peer.incoming.Writer.WriteAsync(([], WebSocketMessageType.Close, true), cancellationToken);
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        state = WebSocketState.CloseSent;
        await peer.incoming.Writer.WriteAsync(([], WebSocketMessageType.Close, true), cancellationToken);
    }

    public override void Abort()
    {
        // 骤断两端都收:relay 正 ReadAsync 的那一头拿到 ChannelClosedException → WebSocketException。
        state = WebSocketState.Aborted;
        incoming.Writer.TryComplete();
        peer.incoming.Writer.TryComplete();
    }

    public override void Dispose()
    {
        incoming.Writer.TryComplete();
    }
}

/// <summary>不经过 HTTP:直接驱动中继,断言断连后依然记账。记账的口径就是设计 §5.5 的折算。</summary>
public class DashScopeSpeechRelayTests
{
    private static readonly string AsrModel = "qwen-audio-3.1-asr-flash-streaming";


    [Fact]
    public async Task Session_timeout_tells_the_client_why_it_stopped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "ft-speech-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var database = new ServerDatabase(databasePath);
            database.Initialize();
            var store = new AccountStore(database);
            var user = store.CreateUser($"relay-timeout-{Guid.NewGuid():N}@example.com", "hash", PlanTier.Free);
            var device = store.RegisterDevice(user.Id, "fp-timeout", "test-pc").Device!.Id;

            var (clientTest, clientRelay) = FakeWebSocket.CreatePair();
            var (upstreamTest, upstreamRelay) = FakeWebSocket.CreatePair();
            var options = new SpeechOptions
            {
                Enabled = true,
                WsUrl = "ws://speech.test/asr",
                Model = AsrModel,
                QuotaCharsPerSecond = 20,
                MinSeconds = 10,
            };
            var gateway = new DashScopeSpeechGateway(options, "test-key",
                _ => Task.FromResult<WebSocket>(upstreamRelay));

            // 硬顶 1 秒:到点服务端必须主动告诉客户端「为什么停了」,不能只断连接
            var relay = gateway.RelayAsync(clientRelay, user.Id, device, store, database, 1, CancellationToken.None);
            await clientTest.SendTextAsync("{\"type\":\"start\",\"sampleRate\":16000}");
            await upstreamTest.ReceiveTextAsync();   // run-task

            var error = await clientTest.ReceiveTextAsync();
            Assert.Contains("\"code\":\"session_timeout\"", error);
            Assert.Contains("时长上限", error);

            await relay;
        }
        finally
        {
            // 不调 SqliteConnection.ClearAllPools():它会清掉整个进程的连接池,
            // 并行跑的其他测试服务端会瞬间拿到失效连接(表现为登录 500)。
            // 文件被占用就留给 %TEMP%,无害。
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }

    [Fact]
    public async Task Records_usage_even_when_client_disconnects()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "ft-speech-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var database = new ServerDatabase(databasePath);
            database.Initialize();
            var store = new AccountStore(database);
            var user = store.CreateUser($"relay-{Guid.NewGuid():N}@example.com", "hash", PlanTier.Free);
            var device = store.RegisterDevice(user.Id, "fp-relay", "test-pc").Device!.Id;

            var (clientTest, clientRelay) = FakeWebSocket.CreatePair();
            var (upstreamTest, upstreamRelay) = FakeWebSocket.CreatePair();
            var options = new SpeechOptions
            {
                Enabled = true,
                WsUrl = "ws://speech.test/asr",
                Model = AsrModel,
                QuotaCharsPerSecond = 20,
                MinSeconds = 10,
            };
            var gateway = new DashScopeSpeechGateway(options, "test-key",
                _ => Task.FromResult<WebSocket>(upstreamRelay));

            var relay = gateway.RelayAsync(clientRelay, user.Id, device, store, database, 600, CancellationToken.None);

            // 客户端发 start → relay 向上游发 run-task(带模型与采样率)。
            // v81:客户端只报采样率,不再把「翻译目标语言」当识别语种提示发过去。
            await clientTest.SendTextAsync("{\"type\":\"start\",\"sampleRate\":16000}");
            var runTask = await upstreamTest.ReceiveTextAsync();
            Assert.Contains("\"action\":\"run-task\"", runTask);
            Assert.Contains(AsrModel, runTask);
            Assert.Contains("\"sample_rate\":16000", runTask);
            Assert.DoesNotContain("language_hints", runTask);

            // 上游回 task-started → relay 给客户端 ready。
            await upstreamTest.SendTextAsync("{\"header\":{\"event\":\"task-started\"},\"payload\":{}}");
            Assert.Equal("{\"type\":\"ready\"}", await clientTest.ReceiveTextAsync());

            // 音频原样透传。
            await clientTest.SendBinaryAsync(new byte[3200]);
            var buffer = new byte[65536];
            using var audioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var audio = await upstreamTest.ReceiveAsync(buffer, audioTimeout.Token);
            Assert.Equal(3200, audio.Count);
            Assert.Equal(WebSocketMessageType.Binary, audio.MessageType);

            // 终稿 + 用量(会话累计,只在 result-generated 上)。
            await upstreamTest.SendTextAsync(
                "{\"header\":{\"event\":\"result-generated\"},\"payload\":{\"output\":{\"sentence\":"
                + "{\"sentence_id\":3,\"text\":\"你好,世界。\",\"sentence_end\":true}},"
                + "\"usage\":{\"duration\":7,\"input_tokens\":96,\"output_tokens\":17}}}");
            Assert.Contains("\"final\":true", await clientTest.ReceiveTextAsync());
            Assert.Contains("\"seconds\":7", await clientTest.ReceiveTextAsync());

            // 客户端不发 stop 直接消失:relay 必须补发 finish-task 并按见过的用量记账。
            clientTest.Abort();
            var finishTask = await upstreamTest.ReceiveTextAsync();
            Assert.Contains("\"action\":\"finish-task\"", finishTask);

            var result = await relay;
            Assert.True(result.Recorded);
            // duration=7 秒不足 MinSeconds=10,按 10 起算:10 × 20 = 200 字符。
            Assert.Equal(200, result.QuotaChars);
            Assert.Equal(Pricing.TextCostMicros(AsrModel, 96, 17), result.CostMicros);
            Assert.Equal(7.0, result.DurationSeconds);
            Assert.Equal(result.CostMicros, store.ReadTodayCostMicros("speech"));
            Assert.Equal(result.CostMicros, store.ReadTodayCostMicros());
            var usage = store.ReadUsage(user.Id, device);
            Assert.Equal(200, usage.MonthlyChars);
        }
        finally
        {
            // 不调 SqliteConnection.ClearAllPools():它会清掉整个进程的连接池,
            // 并行跑的其他测试服务端会瞬间拿到失效连接(表现为登录 500)。
            // 文件被占用就留给 %TEMP%,无害。
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }
    /// <summary>上游一条 usage 都没报（连上就断、全程静音）时必须按会话墙钟计费。</summary>
    /// <remarks>不这么做的话「连上就砍」可以白嫖上游 ASR 算力：预检已经放行，不能不计账（v81）。</remarks>
    [Fact]
    public async Task Bills_wall_clock_when_upstream_never_reports_usage()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "ft-speech-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var database = new ServerDatabase(databasePath);
            database.Initialize();
            var store = new AccountStore(database);
            var user = store.CreateUser($"relay-wall-{Guid.NewGuid():N}@example.com", "hash", PlanTier.Free);
            var device = store.RegisterDevice(user.Id, "fp-wall", "test-pc").Device!.Id;

            var (clientTest, clientRelay) = FakeWebSocket.CreatePair();
            var (upstreamTest, upstreamRelay) = FakeWebSocket.CreatePair();
            var options = new SpeechOptions
            {
                Enabled = true,
                WsUrl = "ws://speech.test/asr",
                Model = AsrModel,
                QuotaCharsPerSecond = 20,
                MinSeconds = 1,
            };
            var gateway = new DashScopeSpeechGateway(options, "test-key",
                _ => Task.FromResult<WebSocket>(upstreamRelay));

            var relay = gateway.RelayAsync(clientRelay, user.Id, device, store, database, 600, CancellationToken.None);
            await clientTest.SendTextAsync("{\"type\":\"start\",\"sampleRate\":16000}");
            Assert.Contains("\"action\":\"run-task\"", await upstreamTest.ReceiveTextAsync());

            // 不推音频、上游也不回 usage：直接砍掉客户端，看是否照样记账。
            clientTest.Abort();
            Assert.Contains("\"action\":\"finish-task\"", await upstreamTest.ReceiveTextAsync());

            var result = await relay;
            Assert.True(result.Recorded);
            // 墙钟至少有 1 秒(MinSeconds=1 → 20 字符),拿到上限 600 秒为止;不赌精确值,只赌「确实记了账」。
            Assert.InRange(result.QuotaChars, 20, 600 * 20);
            Assert.Equal(result.QuotaChars, store.ReadUsage(user.Id, device).MonthlyChars);
            Assert.Equal(0, result.CostMicros); // 没收到 token 数,成本按 0 记
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }

    /// <summary>一条 JSON 被拆成多帧时必须拼完再解析。</summary>
    /// <remarks>DashScope 长句终稿带 words[]，单帧可达数十 KB 且可能跨帧；截断的 JSON 若直接解析会被当坏帧丢掉（v81）。</remarks>
    [Fact]
    public async Task Assembles_json_split_across_text_frames()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "ft-speech-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var database = new ServerDatabase(databasePath);
            database.Initialize();
            var store = new AccountStore(database);
            var user = store.CreateUser($"relay-frag-{Guid.NewGuid():N}@example.com", "hash", PlanTier.Free);
            var device = store.RegisterDevice(user.Id, "fp-frag", "test-pc").Device!.Id;

            var (clientTest, clientRelay) = FakeWebSocket.CreatePair();
            var (upstreamTest, upstreamRelay) = FakeWebSocket.CreatePair();
            var options = new SpeechOptions
            {
                Enabled = true,
                WsUrl = "ws://speech.test/asr",
                Model = AsrModel,
                QuotaCharsPerSecond = 20,
                MinSeconds = 10,
            };
            var gateway = new DashScopeSpeechGateway(options, "test-key",
                _ => Task.FromResult<WebSocket>(upstreamRelay));

            var relay = gateway.RelayAsync(clientRelay, user.Id, device, store, database, 600, CancellationToken.None);
            await clientTest.SendTextAsync("{\"type\":\"start\",\"sampleRate\":16000}");
            await upstreamTest.ReceiveTextAsync();

            const string payload = "{\"header\":{\"event\":\"result-generated\"},\"payload\":{\"output\":{\"sentence\":"
                + "{\"sentence_id\":9,\"text\":\"跨帧的终稿。\",\"sentence_end\":true}},"
                + "\"usage\":{\"duration\":3,\"input_tokens\":40,\"output_tokens\":12}}}";
            var half = payload.Length / 2;
            await upstreamTest.SendTextFragmentAsync(payload[..half], endOfMessage: false);
            await upstreamTest.SendTextFragmentAsync(payload[half..], endOfMessage: true);

            var frame = await clientTest.ReceiveTextAsync();
            Assert.Contains("\"id\":9", frame);
            Assert.Contains("\"final\":true", frame);
            // 能解析出 id 与 final 就证明拼起来的 JSON 完整;文本字段被 System.Text.Json 转义成 \uXXXX,
            // 因此不再断言中文原文(转义形式与 .NET 版本相关,断言它只会让测试变脆)。

            clientTest.Abort();
            await upstreamTest.ReceiveTextAsync();
            var result = await relay;
            Assert.True(result.Recorded);
            // duration=3 秒不足 MinSeconds=10,按 10 起算:10 × 20 = 200 字符
            Assert.Equal(200, result.QuotaChars);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }

    /// <summary>v127: 坏 start 帧（这里 sampleRate 是小数）只判无效，不能让中继抛 500。</summary>
    [Fact]
    public async Task Malformed_start_frame_does_not_crash_the_relay()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "ft-speech-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var database = new ServerDatabase(databasePath);
            database.Initialize();
            var store = new AccountStore(database);
            var user = store.CreateUser($"relay-badstart-{Guid.NewGuid():N}@example.com", "hash", PlanTier.Free);
            var device = store.RegisterDevice(user.Id, "fp-badstart", "test-pc").Device!.Id;

            var (clientTest, clientRelay) = FakeWebSocket.CreatePair();
            var options = new SpeechOptions
            {
                Enabled = true,
                WsUrl = "ws://speech.test/asr",
                Model = AsrModel,
                QuotaCharsPerSecond = 20,
                MinSeconds = 10,
            };
            var connected = false;
            var gateway = new DashScopeSpeechGateway(options, "test-key", _ =>
            {
                connected = true;
                return Task.FromResult<WebSocket>(FakeWebSocket.CreatePair().RelaySide);
            });

            var relay = gateway.RelayAsync(clientRelay, user.Id, device, store, database, 600, CancellationToken.None);
            await clientTest.SendTextAsync("{\"type\":\"start\",\"sampleRate\":1.5}");
            clientTest.Abort();

            var result = await relay;
            Assert.False(connected);
            // 一条 usage 都没收到也按墙钟记最低计费，证明坏帧之后收尾路径仍然正常。
            Assert.True(result.Recorded);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }

}
