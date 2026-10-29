namespace FastTranslation.Server;

/// <summary>
/// The proxy gateway's own settings. Bound from <c>Server:Gateway</c>. The API key here is the
/// developer's DashScope key and never leaves the server — the client only ever sees a quota.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>Off by default: an unconfigured deployment must not pretend to serve translations.</summary>
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DashScope OpenAI-compatible root. The client appends /chat/completions.</summary>
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    /// <summary>Base URL for the image service; the client posts to this exact URL.</summary>
    public string ImageBaseUrl { get; set; } = "https://dashscope.aliyuncs.com/api/v1/services/aigc/image2image/image-synthesis";

    /// <summary>Vision/OCR is served by a different DashScope path than chat completions.</summary>
    public string OcrBaseUrl { get; set; } =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

    public string Model { get; set; } = "qwen-mt-flash";

    /// <summary>Must have a <see cref="Domain.Pricing"/> entry, otherwise accounting falls back to flash prices.</summary>
    public string OcrModel { get; set; } = "qwen-vl-ocr";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whole-deployment spend ceiling per day, in micro-yuan. Last line of defence against one
    /// abusive account: when the day's cost crosses it the gateway answers 429 and every client
    /// falls back to the user's own key, so the product keeps working while the bill stops.
    /// </summary>
    public long DailyBudgetMicros { get; set; } = 5_000_000;

    /// <summary>Per-account requests per minute — a runaway client must not burn a monthly quota in a loop.</summary>
    public int RequestsPerMinute { get; set; } = 40;

    /// <summary>Largest text we will forward, mirroring the client's own 5000-character cap.</summary>
    public int MaxRequestChars { get; set; } = 5_000;

    /// <summary>
    /// Quota characters charged for one screenshot translation. The image API reports no usage, so
    /// this fixed equivalent is the only way to bill it against the same character allowance.
    /// </summary>
    public int ImageQuotaChars { get; set; } = 1_000;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl) ? "https://dashscope.aliyuncs.com/compatible-mode/v1" : BaseUrl.TrimEnd('/');

    /// <summary>The OCR endpoint is a complete URL, so nothing is appended to it.</summary>
    public string EffectiveOcrBaseUrl =>
        string.IsNullOrWhiteSpace(OcrBaseUrl)
            ? "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation"
            : OcrBaseUrl.Trim();
}
