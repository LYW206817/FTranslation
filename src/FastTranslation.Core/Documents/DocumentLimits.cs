namespace FastTranslation.Core.Documents;

/// <summary>
/// 文档翻译的硬上限。放在 Core 而不是 App：超限提示与预估校验都要能单测。
/// <para>
/// 数字来源：单文件 10MB 是「整包解压后 XDocument 常占 XML 的 5–10 倍」这条实测的预算线
/// （100 页 docx 约 10–50MB 可接受，再大一档就会撞 50MB 空载内存红线）；
/// 20 万字符是按 qwen-mt-flash 单价算出来的「一份大文档 ≈ 0.45 元」的界；
/// 5000 段则是「一段一请求、并发 4」时最慢也要跑完的规模上限。
/// </para>
/// </summary>
public static class DocumentLimits
{
    public const long MaxFileBytes = 10L * 1024 * 1024;
    public const int MaxChars = 200_000;
    public const int MaxSegments = 5_000;

    /// <summary>单段上限与 <see cref="TranslationRequest.MaxInputLength"/> 对齐：网关侧不放宽，客户端也不该超。</summary>
    public const int MaxSegmentChars = TranslationRequest.MaxInputLength;

    /// <summary>
    /// 校验文件是否在可处理范围内。返回 null 表示通过，否则是给用户看的中文原因。
    /// 先查大小再解析：解析一个 50MB 的 docx 就已经把内存吃掉了，来不及再拦。
    /// </summary>
    public static string? CheckFile(long fileBytes)
    {
        if (fileBytes <= 0) return "文件是空的";
        if (fileBytes > MaxFileBytes) return $"文件超过 {MaxFileBytes / (1024 * 1024)} MB，请拆分后再试";
        return null;
    }

    /// <summary>解析完成后按实际内容校验。</summary>
    public static string? CheckContent(int segmentCount, long chars)
    {
        if (segmentCount > MaxSegments) return $"段落太多（{segmentCount} > {MaxSegments}），请拆分后再试";
        if (chars > MaxChars) return $"可译字符太多（{chars} > {MaxChars}），请拆分后再试";
        return null;
    }

    /// <summary>
    /// v127: 单段长度也要在开跑前校验。网关与上游都拒绝超过 5000 字符的请求，而
    /// <see cref="MaxSegmentChars"/> 原来只有声明、没有任何引用：超长段会预检通过，
    /// 然后在重试里白等三轮、整段写回「未翻译」——整份文档只有一个长段时等于全没译。
    /// </summary>
    public static string? CheckSegments(IReadOnlyList<DocumentSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.NeedsTranslation && segment.Source.Length > MaxSegmentChars)
            {
                return $"第 {segment.Index + 1} 段有 {segment.Source.Length:N0} 字符，超过单段 {MaxSegmentChars:N0} 字符上限，请先在原文里拆成多段";
            }
        }
        return null;
    }
}
