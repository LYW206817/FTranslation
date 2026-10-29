using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FastTranslation.Server.Gateway;

/// <summary>What the upstream returned, verbatim. The gateway is a pipe, not a rewriter.</summary>
public sealed record GatewayResponse(int StatusCode, string Body)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

/// <summary>
/// The one place that holds the developer's DashScope key. It forwards a caller's request body with
/// the key attached and never lets the key (or an upstream URL) reach the client.
/// </summary>
public sealed class DashScopeGateway(GatewayOptions options, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler())
    {
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 60),
    };

    public async Task<GatewayResponse> SendChatAsync(string body, CancellationToken ct)
        => await SendAsync(options.EffectiveBaseUrl + "/chat/completions", body, ct).ConfigureAwait(false);

    public async Task<GatewayResponse> SendImageAsync(string body, CancellationToken ct)
        => await SendAsync(options.ImageBaseUrl, body, ct).ConfigureAwait(false);

    /// <summary>
    /// OCR rides the multimodal-generation endpoint, which takes the same bearer key and body shape
    /// (model + input.messages with an image_url), so it is one more pipe rather than a new client.
    /// </summary>
    public async Task<GatewayResponse> SendOcrAsync(string body, CancellationToken ct)
        => await SendAsync(options.EffectiveOcrBaseUrl, body, ct).ConfigureAwait(false);

    /// <summary>
    /// Reads the token counts DashScope reports. A response without usage is not an error — the
    /// caller falls back to an estimate so quota accounting never silently stops.
    /// </summary>
    public static bool TryReadUsage(string? body, out long tokensIn, out long tokensOut)
        => TryReadUsageNamed(body, "prompt_tokens", "completion_tokens", out tokensIn, out tokensOut);

    /// <summary>
    /// The native multimodal endpoint reports <c>input_tokens</c>/<c>output_tokens</c> where the
    /// OpenAI-compatible one says <c>prompt_tokens</c>/<c>completion_tokens</c>. Accept either, so OCR
    /// cost tracking does not silently fall back to a character estimate.
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
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new GatewayResponse((int)response.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Upstream unreachable: report it as a gateway failure, never as a quota event.
            return new GatewayResponse(StatusCodes.Status502BadGateway, string.Empty);
        }
    }

    private static long ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed) ? parsed : 0;

    public void Dispose() => _http.Dispose();
}
