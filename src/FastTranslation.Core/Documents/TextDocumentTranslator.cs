using System.Globalization;
using System.Text;

namespace FastTranslation.Core.Documents;

/// <summary>
/// txt / md 的段落级翻译。切段规则故意只保留「按空行」这一条：Markdown 里空行就是段落边界，
/// 按它切既符合作者意图，也让译文段落结构与原文一致（不重排、不合并标题与列表）。
/// </summary>
public sealed class TextDocumentTranslator(DocumentFormat format = DocumentFormat.Text) : IDocumentTranslator
{
    public DocumentFormat Format { get; } = format;

    public async Task<IReadOnlyList<DocumentSegment>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        // 显式 UTF-8 + 允许替换非法字节：默认编码会按系统 ANSI 读，GBK 文本会被读成乱码，
        // 而乱码一旦送译就是把一份坏文档翻成另一种语言。
        var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, false), cancellationToken).ConfigureAwait(false);
        return Split(text);
    }

    public async Task WriteBackAsync(string sourcePath, string outputPath, IReadOnlyList<DocumentSegment> segments, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Index > 0) builder.Append("\n\n");
            // 失败段加「未翻译」标记后保留原文：用户能立刻看出哪几段没译，而不是拿到一份缺段的文件。
            builder.Append(SegmentTextPolicy.WriteBackText(segment));
        }
        builder.Append('\n');
        await File.WriteAllTextAsync(outputPath, builder.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按空行（连续两个及以上换行，中间允许空格与制表符）切段；段内的单个换行属于该段内容
    /// （Markdown 的软换行），不拆开。写回时统一用空行重排，不保留原文用了几个空行。
    /// </summary>
    internal static IReadOnlyList<DocumentSegment> Split(string text)
    {
        var segments = new List<DocumentSegment>();
        if (string.IsNullOrEmpty(text)) return segments;

        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] is not ('\r' or '\n')) { i++; continue; }
            var runEnd = ConsumeBlankRun(text, i, out var newlines);
            if (newlines < 2) { i = runEnd; continue; }
            Add(text[start..i]);
            start = runEnd;
            i = runEnd;
        }
        Add(text[start..]);
        return segments;

        void Add(string raw)
        {
            var source = raw.TrimEnd('\r', '\n');
            if (source.Length == 0) return;
            var status = SegmentTextPolicy.ShouldTranslate(source) ? SegmentStatus.Pending : SegmentStatus.Skipped;
            segments.Add(new DocumentSegment(segments.Count, segments.Count.ToString(CultureInfo.InvariantCulture), source, null, status));
        }
    }

    /// <summary>吃掉从 <paramref name="from"/> 开始的整段空白行运行，返回运行结束位置并给出换行个数。</summary>
    private static int ConsumeBlankRun(string text, int from, out int newlines)
    {
        newlines = 0;
        var i = from;
        while (i < text.Length)
        {
            if (text[i] == '\r')
            {
                newlines++;
                i++;
                if (i < text.Length && text[i] == '\n') i++;
            }
            else if (text[i] == '\n') { newlines++; i++; }
            else if (text[i] is ' ' or '\t') i++;
            else break;
        }
        return i;
    }
}
