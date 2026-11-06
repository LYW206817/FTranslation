namespace FastTranslation.Server.Domain;

/// <summary>单个模型的标价，单位为人民币元。</summary>
public sealed record ModelPrice(string Model, decimal InputPerMillionTokens, decimal OutputPerMillionTokens, decimal PerImage);

/// <summary>
/// DashScope 标价（华北2 北京，2026-10 价目表）。刻意做成代码里的表而不是
/// 配置项：价格写错会静默地对每个账号多计或少计费，而这些数字是
/// 已公布的契约，不是按部署可调的旋钮。价目表变动时更新此表。
/// </summary>
public static class Pricing
{
    public const string DefaultModel = "qwen-mt-flash";

    private static readonly Dictionary<string, ModelPrice> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen-mt-flash"] = new("qwen-mt-flash", 0.7m, 1.95m, 0m),
        ["qwen-mt-plus"] = new("qwen-mt-plus", 1.8m, 5.4m, 0m),
        ["qwen-mt-lite"] = new("qwen-mt-lite", 0.6m, 1.6m, 0m),
        ["qwen-mt-image-2.0"] = new("qwen-mt-image-2.0", 0m, 0m, 0.004m),
        ["qwen-mt-image"] = new("qwen-mt-image", 0m, 0m, 0.003m),

        // 视觉 OCR：截图作为输入（按输入 token 计费），输出识别出的文字。
        // 数字来自 2026-10 百炼价目表；发版前要再核对一遍价目表，因为
        // 过期条目不会让任何东西崩溃，只会让成本账本失真。
        ["qwen-vl-ocr"] = new("qwen-vl-ocr", 0.043m, 0.072m, 0m),

        // 实时语音识别(POC 实测 7.66s 音频报 usage=96+17 token,约 40 token/音频秒)。
        // 必须进表:For() 对未知模型会静默回落 flash 价,语音用量大,错价就是真金白银。
        ["qwen-audio-3.1-asr-flash-streaming"] = new("qwen-audio-3.1-asr-flash-streaming", 6m, 4.5m, 0m),
    };

    public static ModelPrice For(string? model) =>
        !string.IsNullOrWhiteSpace(model) && Table.TryGetValue(model, out var price)
            ? price
            : Table[DefaultModel];

    /// <summary>
    /// 文本成本，单位为微元（1e-6 元），以便账本保持整数运算。用微元是可行的，
    /// 因为一次 flash 请求约花费 7,000 微元——舍入误差到不了一分钱。
    /// </summary>
    public static long TextCostMicros(string? model, long tokensIn, long tokensOut)
    {
        if (tokensIn <= 0 && tokensOut <= 0) return 0;

        var price = For(model);
        var yuan = (tokensIn * price.InputPerMillionTokens + tokensOut * price.OutputPerMillionTokens) / 1_000_000m;
        return (long)decimal.Round(yuan * 1_000_000m, 0, MidpointRounding.AwayFromZero);
    }

    public static long ImageCostMicros(string? model) =>
        (long)decimal.Round(For(model).PerImage * 1_000_000m, 0, MidpointRounding.AwayFromZero);

    /// <summary>各模型相对 flash 的额度倍率。缺项按 1.0 计，与 <see cref="For"/> 回落 flash 的口径一致。</summary>
    private static readonly Dictionary<string, decimal> QuotaMultipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen-mt-flash"] = 1.0m,
        ["qwen-mt-plus"] = 2.7m,
        ["qwen-mt-lite"] = 0.85m,
    };

    /// <summary>
    /// 把「模型原始字符数」折算成「flash 等价额度字符」。
    /// <para>
    /// 倍率来自实测每字符成本：flash 3.11、plus 8.33、lite 2.60 微元（单价取自 <see cref="Table"/>，
    /// token/字符比取本机划词样本 输入 2.02、输出 0.87）。plus/flash = 2.68、lite/flash = 0.84，
    /// 取整成 2.7 与 0.85——成本比会随输入输出结构在 ±5% 内浮动，不必精确到小数点后两位。
    /// </para>
    /// <para>
    /// 不做折算的话，plus 与 flash 按 1:1 扣额度，用户用 5.9 元就能买走 7.33 元的成本；
    /// 而 lite 反而比它真实成本收得更贵。额度单位统一成 flash 等价字符后，
    /// 「1 额度字符 ≈ 3.1 微元」对任何模型都成立。
    /// </para>
    /// </summary>
    /// <param name="model">请求里的模型名，空或未知按 flash 计。</param>
    /// <param name="rawChars">该请求的原始字符数。</param>
    /// <returns>应记入额度与预检的字符数；非零请求至少记 1，避免小文本被四舍五入成免费。</returns>
    public static int QuotaChars(string? model, int rawChars)
    {
        if (rawChars <= 0) return 0;
        var multiplier = !string.IsNullOrWhiteSpace(model) && QuotaMultipliers.TryGetValue(model, out var found)
            ? found
            : 1.0m;
        return Math.Max(1, (int)decimal.Round(rawChars * multiplier, MidpointRounding.AwayFromZero));
    }
}
