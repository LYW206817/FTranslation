namespace FastTranslation.Core.Lexicon;

/// <summary>
/// 判断选区是否值得查词。只有「单个英文单词」才走词库：带空格的是短语，
/// 带中文/数字/句末标点的是句子，套用词典卡只会把原本一句译文拆得七零八落。
/// </summary>
public static class WordShape
{
    public const int MaxLength = 40;

    /// <summary>选区边界上的引号、逗号、句点等不该参与查词。</summary>
    private static readonly char[] WrappingPunctuation =
    [
        ' ', '\t', '\r', '\n', '"', '\'', '’', '‘', '“', '”', '(', ')', '[', ']', '{', '}',
        ',', '.', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？', '…', '、', '-',
    ];

    /// <summary>
    /// 剥掉首尾标点后若仍是单个英文词，返回用于查词的原拼写；否则返回 null。
    /// 词中间的连字符与撇号保留（well-known、don't），首尾的会被剥掉。
    /// </summary>
    public static string? TryNormalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var span = text.AsSpan().Trim(WrappingPunctuation);
        if (span.Length is 0 or > MaxLength) return null;

        var letters = 0;
        foreach (var ch in span)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { letters++; continue; }
            // 撇号既可能是 don't 的一部分，也可能是选区带出来的引号（已在上面的 Trim 处理过边界）
            if (ch is '\'' or '’' or '-') continue;
            return null;
        }
        return letters == 0 ? null : span.ToString();
    }
}
