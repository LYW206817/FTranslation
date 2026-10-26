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
}

/// <summary>
/// Qwen-MT HTTP adapter. Protocol facts (from docs; re-verify before beta):
/// - single user message only; no system message
/// - translation_options is a TOP-LEVEL JSON field (source_lang/target_lang, English names)
/// - Authorization: Bearer key
/// </summary>
public sealed class QwenMtProvider : ITranslationProvider
{
    public const string ApiKeySecretName = "qwenmt-api-key";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;
    private readonly QwenMtOptions _options;
    private readonly bool _ownsHttp;

    public QwenMtProvider(Func<string?> apiKeyAccessor, QwenMtOptions? options = null, HttpClient? http = null)
    {
        _apiKeyAccessor = apiKeyAccessor ?? (() => null);
        _options = options ?? new QwenMtOptions();
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
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
                httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var translated = await ReadTranslationAsync(response, ct).ConfigureAwait(false);
                    if (translated is null)
                    {
                        return Fail(request, TranslationError.InvalidResponse("content missing"), started);
                    }
                    return new TranslationResult(
                        request.Text, translated,
                        request.SourceLanguage, request.TargetLanguage, request.Model,
                        Environment.TickCount64 - started, FromCache: false);
                }

                var error = ClassifyStatus(response, await SafeReadBodyAsync(response, ct).ConfigureAwait(false));
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
            catch (TaskCanceledException) // HttpClient timeout
            {
                if (attempt == 0) { await DelayAsync(attempt, ct).ConfigureAwait(false); continue; }
                return Fail(request, TranslationError.Network("请求超时"), started);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                if (attempt == 0) { await DelayAsync(attempt, ct).ConfigureAwait(false); continue; }
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

    private static async Task<string?> ReadTranslationAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (message.ValueKind == JsonValueKind.Undefined || !message.TryGetProperty("content", out var content)) return null;
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static Task DelayAsync(int attempt, CancellationToken ct) => Task.Delay(400 * (attempt + 1), ct);

    private static TranslationResult Fail(TranslationRequest request, TranslationError error, long started) => new(
        request.Text, string.Empty, request.SourceLanguage, request.TargetLanguage, request.Model,
        Environment.TickCount64 - started, FromCache: false) { Error = error };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
