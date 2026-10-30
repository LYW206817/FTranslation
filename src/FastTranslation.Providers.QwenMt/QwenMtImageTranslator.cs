using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

public sealed class QwenMtImageOptions
{
    public string BaseUrl { get; init; } = "https://dashscope.aliyuncs.com/api/v1/services/aigc/image2image/image-synthesis";
    public string Model { get; init; } = "qwen-mt-image-2.0";
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>只有开发者网关需要（X-FT-Device）；DashScope 会忽略未知请求头。</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

/// <summary>
/// qwen-mt-image 同步适配器。协议事实（实测）：
/// - 同步端点 image2image/image-synthesis，成功即返回译后图 URL（JPG、与原图同尺寸、24 小时过期）
/// - input.image_url 直接接受 base64 data URI；oss:// 私有对象会被内容审核挡成 DataInspection 错误
/// - 需再用 GET 下载译后图字节；文档标 RPM=1，所以一次 429 只重试一次并退避
/// </summary>
public sealed class QwenMtImageTranslator : IImageTranslationProvider
{
    /// <summary>文档标 RPM=1，所以 429/网络错误只重试一次并退避。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;
    private readonly QwenMtImageOptions _options;
    private readonly bool _ownsHttp;

    public QwenMtImageTranslator(Func<string?> apiKeyAccessor, QwenMtImageOptions? options = null, HttpClient? http = null)
    {
        _apiKeyAccessor = apiKeyAccessor ?? (() => null);
        _options = options ?? new QwenMtImageOptions();
        _ownsHttp = http is null;
        // 与 QwenMtProvider 同一坑：别人给的 client 可能已经发过请求，
        // 那之后设 Timeout 会抛 InvalidOperationException（当年截图翻译"点了没反应"就是这个）。
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
    }

    public async Task<ImageTranslationResult> TranslateImageAsync(ImageTranslationRequest request, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        if (request.PngBytes is null || request.PngBytes.Length == 0)
        {
            return Fail(request, TranslationError.InvalidResponse("空图片"), started);
        }

        var apiKey = _apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Fail(request, TranslationError.Authentication(), started);
        }

        // 请求未指定模型时用 options 的默认模型，避免把空 model 发给上游。
        var effective = string.IsNullOrWhiteSpace(request.Model) ? request with { Model = _options.Model } : request;
        var payload = BuildPayload(effective);

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

                using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (QuotaRefusal.Describe(body) is { } quota)
                    {
                        return Fail(effective, new TranslationError(TranslationErrorCode.QuotaExhausted, quota), started);
                    }
                    var error = ClassifyStatus((int)response.StatusCode, body);
                    if (error.Code == TranslationErrorCode.RateLimited && attempt == 0)
                    {
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    return Fail(effective, error, started);
                }

                if (!TryReadImageUrl(body, out var imageUrl))
                {
                    return Fail(effective, TranslationError.InvalidResponse("output.image_url 缺失"), started);
                }

                return await DownloadAsync(effective, imageUrl, started, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(effective, TranslationError.Cancelled(), started);
            }
            catch (TaskCanceledException) // HttpClient 超时
            {
                if (attempt == 0)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return Fail(effective, TranslationError.Network("请求超时"), started);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return Fail(effective, TranslationError.Network(ex.Message), started);
            }
        }

        return Fail(effective, TranslationError.Network("重试后仍失败"), started);
    }

    /// <summary>译后图在 OSS 上、24 小时过期，必须再 GET 一次拿字节；同一次用户操作复用同一个 HttpClient。</summary>
    private async Task<ImageTranslationResult> DownloadAsync(ImageTranslationRequest request, string imageUrl, long started, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Fail(request, TranslationError.InvalidResponse($"图片下载失败({(int)response.StatusCode})"), started);
            }

            var jpeg = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (jpeg.Length == 0)
            {
                return Fail(request, TranslationError.InvalidResponse("图片下载为空"), started);
            }

            return new ImageTranslationResult(
                jpeg,
                request.SourceLanguage, request.TargetLanguage, request.Model,
                Environment.TickCount64 - started, imageUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(request, TranslationError.Cancelled(), started);
        }
        catch (TaskCanceledException)
        {
            return Fail(request, TranslationError.Network("请求超时"), started);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            return Fail(request, TranslationError.Network(ex.Message), started);
        }
    }

    private string BuildUri() =>
        string.IsNullOrWhiteSpace(_options.BaseUrl) ? new QwenMtImageOptions().BaseUrl : _options.BaseUrl.Trim();

    /// <summary>PNG 字节以 base64 data URI 塞进 input.image_url —— 实测可用，不需要先上传文件。</summary>
    internal static string BuildPayload(ImageTranslationRequest request)
    {
        var payload = new
        {
            model = request.Model,
            input = new
            {
                image_url = "data:image/png;base64," + Convert.ToBase64String(request.PngBytes),
                source_lang = request.SourceLanguage,
                target_lang = request.TargetLanguage,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>成功响应是 {"output":{"image_url":"http://...oss...jpg?Expires=...&Signature=..."},...}。</summary>
    internal static bool TryReadImageUrl(string json, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object) return false;
            if (!output.TryGetProperty("image_url", out var url) || url.ValueKind != JsonValueKind.String) return false;

            var value = url.GetString();
            if (string.IsNullOrWhiteSpace(value)) return false;
            imageUrl = value!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>失败响应形如 {"code":"InvalidParameter.DataInspection","message":"..."}，把 code/message 摘进错误文案。</summary>
    internal static TranslationError ClassifyStatus(int statusCode, string body)
    {
        var detail = ExtractErrorDetail(body);
        return statusCode switch
        {
            401 or 403 => TranslationError.Authentication(),
            429 => TranslationError.RateLimited(),
            >= 400 and < 500 => TranslationError.InvalidResponse(
                string.IsNullOrEmpty(detail) ? $"请求被拒绝({statusCode})" : $"请求被拒绝({statusCode}):{detail}"),
            >= 500 => new TranslationError(TranslationErrorCode.ServerError,
                string.IsNullOrEmpty(detail) ? $"服务端错误({statusCode})" : $"服务端错误({statusCode}):{detail}"),
            _ => new TranslationError(TranslationErrorCode.Unknown, $"HTTP {statusCode}"),
        };
    }

    private static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Clip(body);
            var code = ReadString(doc.RootElement, "code");
            var message = ReadString(doc.RootElement, "message");
            if (code.Length > 0 && message.Length > 0) return Clip($"{code}:{message}");
            return Clip(code.Length > 0 ? code : message);
        }
        catch (JsonException)
        {
            return Clip(body);
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Clip(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static ImageTranslationResult Fail(ImageTranslationRequest request, TranslationError error, long started) => new(
        Array.Empty<byte>(),
        request.SourceLanguage, request.TargetLanguage, request.Model,
        Environment.TickCount64 - started, string.Empty) { Error = error };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
