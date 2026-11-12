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
    /// v129: 客户端可以指定 model 并把网关当通用 LLM 用；输出长度不设上限时，
    /// 输入只按字符计费、输出 token 完全不进额度，成本会与账本脱节。
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4_096;

    /// <summary>chat 请求体上限：5000 字符正文加 JSON 结构与翻译参数，256 KB 足够。</summary>
    public int MaxChatBodyBytes { get; set; } = 256 * 1024;

    /// <summary>图片/OCR 请求体上限：截图以 base64 直传，16 MB 覆盖大图且低于 Kestrel 的 30 MB。</summary>
    public int MaxImageBodyBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// 截图翻译中图片翻译与 OCR 各自扣除的额度字符数。图像 API 不报告用量，所以
    /// 这个固定折算值是把它计入同一套字符额度的唯一方式；两个调用合起来就是
    /// 产品口径的「每张截图 2,000 字符起」，再加上 OCR 文字的文本翻译费。
    /// OCR 失败时只扣图片这一份，不会强收 OCR 的 1,000。
    /// </summary>
    public int ImageQuotaChars { get; set; } = 1_000;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>语音中继的开关与参数。配置路径必须写全 Server:Gateway:Speech:*(根节是 Server,
    /// 只写 Gateway:Speech:* 会静默绑不上——这是最小 API 选项绑定的既有约定)。</summary>
    public SpeechOptions Speech { get; set; } = new();

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl) ? "https://dashscope.aliyuncs.com/compatible-mode/v1" : BaseUrl.TrimEnd('/');

    /// <summary>OCR 端点是完整 URL，因此不在其后追加任何路径。</summary>
    public string EffectiveOcrBaseUrl =>
        string.IsNullOrWhiteSpace(OcrBaseUrl)
            ? "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation"
            : OcrBaseUrl.Trim();
}


/// <summary>语音中继配置(设计 §6.2)。WsUrl 整串可配,避免代码里拼 WorkspaceId。</summary>
public sealed class SpeechOptions
{
    public bool Enabled { get; set; }

    public string WsUrl { get; set; } = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";

    /// <summary>DashScope 工作空间 id。空则不带工作空间头。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>实时 ASR 模型。POC 已实证 qwen-audio-3.1-asr-flash-streaming 可用。</summary>
    public string Model { get; set; } = "qwen-audio-3.1-asr-flash-streaming";

    /// <summary>额度折算:一秒音频折成多少字符(与文本翻译同池子,只是换算口径)。
    /// v109: 20→50。20 字符/秒只覆盖 ASR 成本的 26%~73%，50 覆盖 65%~181%；
    /// 客户端 FastTranslation.Core/Speech/SpeechQuotaGate.CharsPerSecond 是同值镜像,必须一起改。</summary>
    public int QuotaCharsPerSecond { get; set; } = 50;

    /// <summary>最短按多少秒起算:讲半秒也说了一句话,不能按 0 记。
    /// v98: 10 秒改 5 秒,是为了让语音「暂停→继续」每轮少付一半保底;
    /// v109 费率改为 50 字符/秒后,保底从 500→250 字符。只说 1 秒仍记 250 字符
    /// > 折算的 50 字符,保底依然是超记,没打开套利口子。
    /// 客户端 FastTranslation.Core/Speech/SpeechQuotaGate.MinBilledSeconds 是同值镜像(跨不进同一编译单元),改这里必须一起改,否则浮窗用量显示比服务端少记。</summary>
    public int MinSeconds { get; set; } = 5;

    /// <summary>单会话硬顶秒数:到点由服务端发 finish-task 关流,防连接挂着烧钱。</summary>
    public int MaxSecondsPerSession { get; set; } = 1800;

    /// <summary>同一账号同时能开几条语音会话(0=不限制,默认 1)。
    /// 限流器按请求数计数,拦不住并发会话带来的成本放大——这是第二道闸(v81)。</summary>
    public int MaxConcurrentSessionsPerUser { get; set; } = 1;

    /// <summary>语音独立日预算(微元)。默认 2 元/日≈2.3 小时:全站 5 元桶只够 5.8 小时
    /// 且语音烧完会把文本翻译一起 429,所以单独一个桶,先撞预算是刻意的。</summary>
    public long DailyBudgetMicros { get; set; } = 2_000_000;
}