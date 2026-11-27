using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Gateway;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>把单账号并发名额压到 1 的工厂。</summary>
public class TightGatewayFactory : GatewayFactory
{
    protected override int MaxConcurrentPerUser => 1;
}

public class GatewayConcurrencyTests : IClassFixture<TightGatewayFactory>
{
    private readonly TightGatewayFactory _factory;

    public GatewayConcurrencyTests(TightGatewayFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _factory.CreateClient();
        var email = $"tight-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> ChatAsync(HttpClient client, int chars) =>
        client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = new string('a', chars) } },
            translation_options = new { source_lang = "en", target_lang = "zh" },
        });

    [Fact]
    public async Task A_second_in_flight_request_from_the_same_account_is_refused()
    {
        var client = await SignedInAsync();
        using var gate = new ManualResetEventSlim(false);
        _factory.Upstream.Responder = (_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return StubUpstreamHandler.Json("""{"choices":[{"message":{"content":"ok"}}]}""");
        };

        try
        {
            var first = ChatAsync(client, 10);
            await WaitUntilAsync(() => _factory.Upstream.Calls.Count >= 1, TimeSpan.FromSeconds(5));

            // 第一条还卡在上游，占着该账号唯一的并发名额。
            var second = await ChatAsync(client, 10);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

            gate.Set();
            (await first).EnsureSuccessStatusCode();
        }
        finally
        {
            gate.Set();
            _factory.Upstream.Reset();
        }
    }

    [Fact]
    public async Task ReadBody_aborts_a_stream_that_dribbles_past_the_timeout()
    {
        // TestServer 会先缓冲整包，所以超时只能在流这一层直接验。
        await using var slow = new SlowReadStream(chunks: 100, chunkSize: 1024, delay: TimeSpan.FromMilliseconds(100));

        var (body, status) = await GatewayEndpoints.ReadBodyWithTimeoutAsync(
            slow, maxBytes: 1_000_000, timeoutSeconds: 1, CancellationToken.None);

        Assert.Null(body);
        Assert.Equal(GatewayEndpoints.BodyReadStatus.TimedOut, status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not met in time");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>每次读取前等待一段时间，模拟略高于 Kestrel 最低速率的滴流。</summary>
    private sealed class SlowReadStream(int chunks, int chunkSize, TimeSpan delay) : Stream
    {
        private readonly byte[] _payload = new byte[chunkSize];
        private int _remaining = chunks;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining-- <= 0)
            {
                return 0;
            }

            await Task.Delay(delay, ct);
            var count = Math.Min(chunkSize, buffer.Length);
            _payload.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
