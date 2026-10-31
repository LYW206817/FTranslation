using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace FastTranslation.Core.Documents;

/// <summary>
/// docx 的读写实现。只依赖 BCL 的 <see cref="ZipArchive"/> 与 <see cref="XDocument"/>：
/// 引入 OpenXml SDK 要多背 7.8MB 的 dll 并带反射重、trim 风险，而这里要做的只是
/// 「找到段落 → 改 <c>w:t</c> 的文字」，BCL 完全够用。
/// </summary>
public sealed class DocxDocumentTranslator : IDocumentTranslator
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly XName[] NonTextRunChildren = [W + "drawing", W + "pict", W + "object", W + "fldChar"];

    private const string DocumentPart = "word/document.xml";

    /// <inheritdoc />
    public DocumentFormat Format => DocumentFormat.Docx;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSegment>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        // 解析是纯同步的 CPU/IO 活：放到线程池，别让调用方（UI 线程）在几百段文档上僵住。
        return await Task.Run(() => Load(path), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteBackAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<DocumentSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(segments);
        // 源文件只读：先复制再改副本，中途失败也留着一份完好的原稿。
        File.Copy(sourcePath, outputPath, overwrite: true);
        await Task.Run(() => Write(outputPath, segments, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<DocumentSegment> Load(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var segments = new List<DocumentSegment>();
        foreach (var entry in ContentParts(archive))
        {
            var document = LoadPart(entry);
            var ordinal = -1;
            foreach (var paragraph in document.Descendants(W + "p"))
            {
                ordinal++;
                var source = ExtractText(paragraph);
                if (source.Length == 0) continue;
                var status = SegmentTextPolicy.ShouldTranslate(source) ? SegmentStatus.Pending : SegmentStatus.Skipped;
                segments.Add(new DocumentSegment(segments.Count, ParagraphPath(entry.FullName, ordinal), source, null, status));
            }
        }

        return segments;
    }

    private static void Write(string outputPath, IReadOnlyList<DocumentSegment> segments, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        // 只处理真的被翻译过或失败过的段：Skipped 什么都不做（原文已经是对的）。
        var groups = segments
            .Where(s => s.Status is SegmentStatus.Done or SegmentStatus.Failed)
            .GroupBy(s => PartNameOf(s.Path), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (archive.GetEntry(group.Key) is not { } entry) continue;
            var document = LoadPart(entry);
            var paragraphs = document.Descendants(W + "p").ToList();
            foreach (var segment in group)
            {
                if (ReplacementFor(segment) is not { } replacement) continue;
                // 序号必须与解析时一致：段落序号取该 part 内所有 w:p 的原始位置，
                // 被跳过的段落（例如只含图片）照样占一个号，否则回写会整体错位一段。
                var ordinal = ParagraphOrdinal(segment.Path);
                if (ordinal < 0 || ordinal >= paragraphs.Count) continue;
                Apply(paragraphs[ordinal], replacement);
            }

            SavePart(entry, document);
        }
    }

    private static string? ReplacementFor(DocumentSegment segment) => segment.Status switch
    {
        SegmentStatus.Done when segment.Target is { Length: > 0 } target => target,
        SegmentStatus.Failed => SegmentTextPolicy.UntranslatedMarker + segment.Source,
        _ => null,
    };

    /// <summary>
    /// 把译文写进段落的第一个纯文本 run，清掉其余纯文本 run 的文字。
    /// 为什么不清掉整个 run：run 里可能还挂着 <c>w:br</c> / <c>w:tab</c>，
    /// 整段删掉会让原本靠换行断开的段落粘成一句。
    /// </summary>
    private static void Apply(XElement paragraph, string replacement)
    {
        var runs = TextRuns(paragraph).ToList();
        if (runs.Count == 0) return;
        foreach (var run in runs.Skip(1))
        {
            foreach (var text in run.Elements(W + "t").ToList()) text.Remove();
            if (!run.Elements().Any()) run.Remove();
        }

        var first = runs[0].Element(W + "t");
        if (first is null) return;
        first.Value = replacement;
        // 译文经常以空格开头/结尾，不显式 declare 会被 Word 吞掉首尾空白。
        first.SetAttributeValue(XNamespace.Xml + "space", "preserve");
    }

    private static IEnumerable<XElement> TextRuns(XElement paragraph) =>
        paragraph.Descendants(W + "r").Where(IsPlainTextRun);

    private static bool IsPlainTextRun(XElement run) =>
        run.Element(W + "t") is not null && !run.Elements().Any(e => NonTextRunChildren.Contains(e.Name));

    private static string ExtractText(XElement paragraph)
    {
        var text = string.Concat(TextRuns(paragraph).Select(run => run.Element(W + "t")!.Value));
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    private static IEnumerable<ZipArchiveEntry> ContentParts(ZipArchive archive) =>
        archive.Entries
            .Where(entry => IsContentPart(entry.FullName))
            // 正文优先：段落序号按 part 各自计数，顺序只影响日志可读性与失败时的处理先后。
            .OrderBy(entry => entry.FullName == DocumentPart ? 0 : 1)
            .ThenBy(entry => entry.FullName, StringComparer.Ordinal);

    /// <summary>
    /// 参与翻译的 part：正文、页眉、页脚、脚注、尾注。
    /// 刻意不处理 <c>comments.xml</c>：批注按段翻译会破坏批注与锚点的对应关系，
    /// 而用户真正要的是正文译版。
    /// </summary>
    private static bool IsContentPart(string name)
    {
        if (!name.StartsWith("word/", StringComparison.Ordinal) || !name.EndsWith(".xml", StringComparison.Ordinal)) return false;
        var stem = name[5..^4];
        return stem is "document" or "footnotes" or "endnotes"
            || stem.StartsWith("header", StringComparison.Ordinal)
            || stem.StartsWith("footer", StringComparison.Ordinal);
    }

    private static XDocument LoadPart(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        // PreserveWhitespace：xml:space="preserve" 的段落里，空格本身就是内容。
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void SavePart(ZipArchiveEntry entry, XDocument document)
    {
        using var stream = entry.Open();
        // v72: Update 模式下 Open() 不裁掉旧内容，只写新内容更短时会残留旧字节，
        // 生成的 docx 直接打不开（Word 报"内容有问题"）。必须先显式截断。
        stream.SetLength(0);
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static string ParagraphPath(string partName, int ordinal) =>
        "/" + partName + "#p" + ordinal.ToString(CultureInfo.InvariantCulture);

    private static string PartNameOf(string path)
    {
        var hash = path.LastIndexOf('#');
        return (hash < 0 ? path : path[..hash]).TrimStart('/');
    }

    private static int ParagraphOrdinal(string path)
    {
        var hash = path.LastIndexOf("#p", StringComparison.Ordinal);
        if (hash < 0) return -1;
        return int.TryParse(path[(hash + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
            ? ordinal
            : -1;
    }
}
