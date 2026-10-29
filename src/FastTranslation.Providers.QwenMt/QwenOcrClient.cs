using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

public sealed class QwenOcrOptions
{
    public string BaseUrl { get; init; } =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

    public string Model { get; init; } = "qwen-vl-ocr";

    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>只有开发者网关需要（X-FT-Device）；DashScope 会忽略未知请求头。</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

/// <summary>
/// qwen-vl-ocr 同步适配器。协议事实（与图片翻译同一条多模态链路，但返回文本而非图片）：
/// - 端点 multimodal-generation/generation，model=qwen-vl-ocr
/// - 不传 prompt 时默认行为就是「只输出图中文字」，这正是截图取词要的
/// - 成功响应 output.choices[0].message.content[0].text，没有需要再下载的远程资源
/// - 与图片翻译一样只重试一次（429/网络错误），因为配额紧张时狂重试只是排队
/// </summary>
public sealed class QwenOcrClient : IImageOcrProvider
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;
    private readonly QwenOcrOptions _options;
    private readonly bool _ownsHttp;

    public QwenOcrClient(Func<string?> apiKeyAccessor, QwenOcrOptions? options = null, HttpClient? http = null)
    {
        _apiKeyAccessor = apiKeyAccessor ?? (() => null);
        _options = options ?? new QwenOcrOptions();
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<ImageOcrResult> RecognizeAsync(ImageOcrRequest request, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        if (request.PngBytes is null || request.PngBytes.Length == 0)
        {
            return ImageOcrResult.Failed(TranslationError.InvalidResponse("空图片"), started);
        }

        var apiKey = _apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ImageOcrResult.Failed(TranslationError.Authentication(), started);
        }

        var payload = BuildPayload(request, _options.Model);

        for (var attempt = 0; attempt < 2; attempt++)
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

                using var response = await _http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (QuotaRefusal.Describe(body) is { } quota)
                    {
                        return ImageOcrResult.Failed(
                            new TranslationError(TranslationErrorCode.QuotaExhausted, quota), started);
                    }

                    var error = QwenMtImageTranslator.ClassifyStatus((int)response.StatusCode, body);
                    if (error.Code == TranslationErrorCode.RateLimited && attempt == 0)
                    {
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return ImageOcrResult.Failed(error, started);
                }

                if (!TryReadText(body, out var text))
                {
                    // 成功状态码但没有文字：截图里可能本来就没有可识别的字，这不是网络故障。
                    return ImageOcrResult.Failed(TranslationError.InvalidResponse("图里没有识别到文字"), started);
                }

                return new ImageOcrResult(text, Environment.TickCount64 - started, string.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ImageOcrResult.Failed(TranslationError.Cancelled(), started);
            }
            catch (TaskCanceledException) // HttpClient 超时
            {
                if (attempt == 0)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return ImageOcrResult.Failed(TranslationError.Network("请求超时"), started);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return ImageOcrResult.Failed(TranslationError.Network(ex.Message), started);
            }
        }

        return ImageOcrResult.Failed(TranslationError.Network("重试后仍失败"), started);
    }

    private string BuildUri() =>
        string.IsNullOrWhiteSpace(_options.BaseUrl) ? new QwenOcrOptions().BaseUrl : _options.BaseUrl.Trim();

    /// <summary>PNG 以 base64 data URI 放进 input.messages[0].content[0].image_url；不带 prompt 就是纯取字。</summary>
    internal static string BuildPayload(ImageOcrRequest request, string model) => JsonSerializer.Serialize(new
    {
        model,
        input = new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { image_url = "data:image/png;base64," + Convert.ToBase64String(request.PngBytes) },
                    },
                },
            },
        },
    });

    /// <summary>
    /// 成功响应 output.choices[0].message.content[0].text。
    /// content 也兼容纯字符串形态（OpenAI 兼容模式会那样返回），任何一层缺失都返回 false。
    /// </summary>
    internal static bool TryReadText(string json, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object) return false;
            if (!output.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return false;
            if (choices.GetArrayLength() == 0) return false;

            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return false;
            if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("content", out var content)) return false;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var node)
                        && node.ValueKind == JsonValueKind.String)
                    {
                        var value = node.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value!);
                    }
                }

                text = string.Join("\n", parts).Trim();
                return text.Length > 0;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                text = (content.GetString() ?? string.Empty).Trim();
                return text.Length > 0;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
