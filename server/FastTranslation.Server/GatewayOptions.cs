namespace FastTranslation.Server;

/// <summary>
/// 代理网关自身的设置。绑定自 <c>Server:Gateway</c>。这里的 API Key 是
/// 开发者的 DashScope Key，永不离开服务端——客户端只会看到额度。
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>默认关闭：未配置的部署不能假装在提供翻译服务。</summary>
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DashScope 的 OpenAI 兼容根地址。客户端会追加 /chat/completions。</summary>
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    /// <summary>图像服务的基础 URL；客户端直接向这个完整 URL 发 POST。</summary>
    public string ImageBaseUrl { get; set; } = "https://dashscope.aliyuncs.com/api/v1/services/aigc/image2image/image-synthesis";

    /// <summary>视觉/OCR 由另一条 DashScope 路径提供，与 chat completions 不同。</summary>
    public string OcrBaseUrl { get; set; } =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

    public string Model { get; set; } = "qwen-mt-flash";

    /// <summary>必须在 <see cref="Domain.Pricing"/> 中有条目，否则计费会回落到 flash 的价格。</summary>
    public string OcrModel { get; set; } = "qwen-vl-ocr";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 整个部署每日的支出上限，单位为微元。这是针对单个
    /// 滥用账号的最后防线：当日成本越过该上限后，网关返回 429，所有客户端
    /// 回落到用户自己的 Key，于是账单停住而产品继续可用。
    /// </summary>
    public long DailyBudgetMicros { get; set; } = 5_000_000;

    /// <summary>每账号每分钟请求数——失控的客户端不能靠循环烧掉整个月额度。</summary>
    public int RequestsPerMinute { get; set; } = 40;

    /// <summary>允许转发的最大文本长度，与客户端自身的 5000 字符上限一致。</summary>
    public int MaxRequestChars { get; set; } = 5_000;

    /// <summary>
    /// 一次截图翻译扣除的额度字符数。图像 API 不报告用量，所以
    /// 这个固定折算值是把它计入同一套字符额度的唯一方式。
    /// </summary>
    public int ImageQuotaChars { get; set; } = 1_000;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl) ? "https://dashscope.aliyuncs.com/compatible-mode/v1" : BaseUrl.TrimEnd('/');

    /// <summary>OCR 端点是完整 URL，因此不在其后追加任何路径。</summary>
    public string EffectiveOcrBaseUrl =>
        string.IsNullOrWhiteSpace(OcrBaseUrl)
            ? "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation"
            : OcrBaseUrl.Trim();
}
