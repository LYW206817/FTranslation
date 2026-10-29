using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

public sealed class QwenMtOptions
{
    public string BaseUrl { get; init; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string Model { get; init; } = "qwen-mt-flash";
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// 每个请求都带的请求头。只有开发者网关需要：设备标识让按设备的每日额度记到真正
    /// 发出文本的那台设备上。DashScope 本身会忽略未知请求头，所以一个类同时服务两条链路。
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

/// <summary>
/// Qwen-MT 的 HTTP 适配器。协议事实（来自文档；beta 前需再核对）：
/// - 只发一条 user 消息；没有 system 消息
/// - translation_options 是顶层 JSON 字段（source_lang/target_lang，用英文语言名）
/// - 认证请求头形如 Authorization: Bearer key
/// </summary>
public sealed class QwenMtProvider : ITranslationProvider
{
    public const string ApiKeySecretName = "qwenmt-api-key";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;
    private readonly QwenMtOptions _options;
    private readonly bool _ownsHttp;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    public QwenMtProvider(Func<string?> apiKeyAccessor, QwenMtOptions? options = null, HttpClient? http = null)
    {
        _apiKeyAccessor = apiKeyAccessor ?? (() => null);
        _options = options ?? new QwenMtOptions();
        _ownsHttp = http is null;
        // 传进来的 client 可能已经发过请求，而 HttpClient 在那之后拒绝再改 Timeout
        // （InvalidOperationException: This instance has already started one or more requests）。
        // 自己造的那一个才归我们设超时；别人给的按别人的策略来。
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        if (request.Text.Length > TranslationRequest.MaxInputLength)
        {
            return Fail(request, TranslationError.ContentTooLong(request.Text.Length, TranslationRequest.MaxInputLength), started);
        }

        var apiKey = _apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Fail(request, new TranslationError(TranslationErrorCode.Authentication, "未配置 API Key"), started);
        }

        var payload = BuildPayload(request);
        int attempt;
        for (attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri());
                httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                foreach (var header in _options.ExtraHeaders ?? EmptyHeaders)
                {
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var (translated, usage) = await ReadTranslationAsync(response, ct).ConfigureAwait(false);
                    if (translated is null)
                    {
                        return Fail(request, TranslationError.InvalidResponse("content missing"), started);
                    }
                    return new TranslationResult(
                        request.Text, translated,
                        request.SourceLanguage, request.TargetLanguage, request.Model,
                        Environment.TickCount64 - started, FromCache: false)
                    {
                        Usage = usage,
                    };
                }

                var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                // 网关拒绝额度用尽时同样用 429，所以只能靠响应体区分这两种情况。
                // 重试一次已用完的额度只是浪费用户时间，而组合 provider 需要知道
                // 这意味着「改用你自己的 Key」。
                if (QuotaRefusal.Describe(body) is { } quota)
                {
                    return Fail(request, new TranslationError(TranslationErrorCode.QuotaExhausted, quota), started);
                }

                var error = ClassifyStatus(response, body);
                if (error.Code == TranslationErrorCode.RateLimited && attempt == 0)
                {
                    await DelayAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return Fail(request, error, started);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Fail(request, TranslationError.Cancelled(), started);
            }
            catch (TaskCanceledException) // HttpClient 超时
            {
                if (attempt == 0) { await DelayAsync(attempt, ct).ConfigureAwait(false); continue; }
                return Fail(request, TranslationError.Network("请求超时"), started);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                if (attempt == 0 && IsWorthRetrying(ex)) { await DelayAsync(attempt, ct).ConfigureAwait(false); continue; }
                return Fail(request, TranslationError.Network(ex.Message), started);
            }
        }
        return Fail(request, TranslationError.Network("重试后仍失败"), started);
    }

    internal string BuildUri()
    {
        var base_ = string.IsNullOrWhiteSpace(_options.BaseUrl) ? new QwenMtOptions().BaseUrl : _options.BaseUrl.TrimEnd('/');
        return base_ + "/chat/completions";
    }

    internal static string BuildPayload(TranslationRequest request)
    {
        var payload = new
        {
            model = request.Model,
            messages = new object[]
            {
                new { role = "user", content = request.Text },
            },
            translation_options = new
            {
                source_lang = request.SourceLanguage,
                target_lang = request.TargetLanguage,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static async Task<(string? Text, TokenUsage? Usage)> ReadTranslationAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return (null, null);
            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (message.ValueKind == JsonValueKind.Undefined || !message.TryGetProperty("content", out var content)) return (null, null);
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            return (text, ReadUsage(root));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 用量就带在同一个响应体里，读它不增加额外开销。字段缺失表示消费方保留自己的估算，
    /// 而不是上报一个编造的零。
    /// </summary>
    internal static TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        var input = ReadTokenCount(usage, "prompt_tokens");
        var output = ReadTokenCount(usage, "completion_tokens");
        return input is null && output is null ? null : new TokenUsage(input ?? 0, output ?? 0);
    }

    private static long? ReadTokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count)
            ? count
            : null;

    private static TranslationError ClassifyStatus(HttpResponseMessage response, string body)
    {
        var code = (int)response.StatusCode;
        return code switch
        {
            401 or 403 => TranslationError.Authentication(),
            429 => TranslationError.RateLimited(),
            >= 400 and < 500 => new TranslationError(TranslationErrorCode.InvalidResponse, $"请求被拒绝({code})"),
            >= 500 => new TranslationError(TranslationErrorCode.ServerError, $"服务端错误({code})"),
            _ => new TranslationError(TranslationErrorCode.Unknown, $"HTTP {code}"),
        };
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 连接被拒、域名解析不出来，重试一次不会变好，只会让用户多等一轮；超时和连接被重置才值得重试。
    /// </summary>
    private static bool IsWorthRetrying(Exception exception)
    {
        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        return socket?.SocketErrorCode is not (SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NoData);
    }

    private static Task DelayAsync(int attempt, CancellationToken ct) => Task.Delay(400 * (attempt + 1), ct);

    private static TranslationResult Fail(TranslationRequest request, TranslationError error, long started) => new(
        request.Text, string.Empty, request.SourceLanguage, request.TargetLanguage, request.Model,
        Environment.TickCount64 - started, FromCache: false) { Error = error };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
