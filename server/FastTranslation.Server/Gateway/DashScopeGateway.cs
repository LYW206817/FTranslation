using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FastTranslation.Server.Gateway;

/// <summary>上游返回的原样内容。网关只负责转发，不负责改写。</summary>
public sealed record GatewayResponse(int StatusCode, string Body, bool OutcomeUnknown = false, string? UpstreamRequestId = null)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

/// <summary>
/// 唯一持有开发者 DashScope Key 的地方。它把调用方的请求体连同
/// 该 Key 一起转发，绝不让 Key（或上游 URL）到达客户端。
/// </summary>
public sealed class DashScopeGateway(GatewayOptions options, HttpMessageHandler? handler = null, CancellationToken stopping = default) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(options.EffectiveTimeoutSeconds);

    public async Task<GatewayResponse> SendChatAsync(string body, CancellationToken ct)
        => await SendAsync(options.EffectiveBaseUrl + "/chat/completions", body, ct).ConfigureAwait(false);

    public async Task<GatewayResponse> SendImageAsync(string body, CancellationToken ct)
        => await SendAsync(options.ImageBaseUrl, body, ct).ConfigureAwait(false);

    /// <summary>
    /// OCR 走 multimodal-generation 端点，它使用相同的 bearer key 和请求体结构
    /// （model + 带 image_url 的 input.messages），所以只是多一条转发通道，而不是新客户端。
    /// </summary>
    public async Task<GatewayResponse> SendOcrAsync(string body, CancellationToken ct)
        => await SendAsync(options.EffectiveOcrBaseUrl, body, ct).ConfigureAwait(false);

    /// <summary>
    /// 读取 DashScope 报告的 token 数。响应里没有 usage 不算错误——
    /// 调用方会回落到估算值，这样额度记账永远不会悄悄停摆。
    /// </summary>
    public static bool TryReadUsage(string? body, out long tokensIn, out long tokensOut)
        => TryReadUsageNamed(body, "prompt_tokens", "completion_tokens", out tokensIn, out tokensOut);

    /// <summary>
    /// 原生多模态端点报告的是 <c>input_tokens</c>/<c>output_tokens</c>，而
    /// OpenAI 兼容端点用的是 <c>prompt_tokens</c>/<c>completion_tokens</c>。两者都接受，这样 OCR
    /// 成本统计不会悄悄回落到字符估算。
    /// </summary>
    public static bool TryReadVisionUsage(string? body, out long tokensIn, out long tokensOut)
        => TryReadUsageNamed(body, "input_tokens", "output_tokens", out tokensIn, out tokensOut)
            || TryReadUsageNamed(body, "prompt_tokens", "completion_tokens", out tokensIn, out tokensOut);

    private static bool TryReadUsageNamed(string? body, string inName, string outName, out long tokensIn, out long tokensOut)
    {
        tokensIn = 0;
        tokensOut = 0;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            tokensIn = ReadLong(usage, inName);
            tokensOut = ReadLong(usage, outName);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<GatewayResponse> SendAsync(string url, string body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        int? status = null;
        string? upstreamRequestId = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(_timeout);
        try
        {
            if (deadline.IsCancellationRequested)
            {
                return new GatewayResponse(StatusCodes.Status503ServiceUnavailable, """{"error":"upstream_unavailable"}""");
            }
            // 先取响应头，正文中断时仍可能有对账标识。独立截止时间覆盖整个下载过程。
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            status = (int)response.StatusCode;
            upstreamRequestId = ReadRequestId(response.Headers);
            var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return new GatewayResponse(status.Value, text, OutcomeUnknown: status >= 500 || status == 408,
                UpstreamRequestId: upstreamRequestId ?? ReadRequestId(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException)
        {
            // 已收到明确拒绝的响应头时，正文读取失败也不会改变这笔的结算状态。
            var rejected = status is >= 400 and < 500 and not 408;
            if (status is null && FailedBeforeSending(ex))
            {
                return new GatewayResponse(StatusCodes.Status503ServiceUnavailable, """{"error":"upstream_unavailable"}""");
            }
            return new GatewayResponse(rejected ? status.GetValueOrDefault() : StatusCodes.Status502BadGateway,
                string.Empty, OutcomeUnknown: !rejected, UpstreamRequestId: upstreamRequestId);
        }
    }

    private static bool FailedBeforeSending(Exception failure)
    {
        var socket = failure as SocketException ?? failure.InnerException as SocketException;
        // 仅把明确没有建立连接的错误视为拒绝。连接被重置也可能发生在提交之后。
        return failure is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            || socket?.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NoData;
    }

    private static string? ReadRequestId(HttpResponseHeaders headers) =>
        headers.TryGetValues("x-request-id", out var values) ? SafeRequestId(values.FirstOrDefault()) : null;

    private static string? ReadRequestId(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("request_id", out var value)
                && value.ValueKind == JsonValueKind.String ? SafeRequestId(value.GetString()) : null;
        }
        catch (JsonException)
        {
            // 非 JSON 的上游错误正文仍按原样处理。
            return null;
        }
    }

    private static string? SafeRequestId(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value : null;

    private static long ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed) ? parsed : 0;

    public void Dispose() => _http.Dispose();
}
