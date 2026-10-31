using System.IO.Compression;
using System.Xml.Linq;
using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

/// <summary>
/// 用真实的 <c>Fixtures/sample.docx</c> 当输入：手搓 XML 会漏掉 Word 与 python-docx
/// 真的会写出来的结构（表格里的 <c>w:p</c>、只含图片的段落、页眉 part），
/// 而回写错位、docx 打不开这类故障只能在真文件上表现出来。
/// </summary>
public sealed class DocxDocumentTranslatorTests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ft-tests-" + Guid.NewGuid().ToString("N"));

    public DocxDocumentTranslatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 临时目录删不掉不影响用例结论。
        }
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Documents", "Fixtures", "sample.docx");

    private static readonly DocxDocumentTranslator Translator = new();

    [Fact]
    public async Task Load_returns_one_segment_per_text_paragraph()
    {
        var segments = await Translator.LoadAsync(FixturePath, CancellationToken.None);

        // document.xml 有 9 个 w:p，其中一个是纯图片段落（无 w:t），被跳过；
        // header1.xml 再多一段。
        Assert.Equal(9, segments.Count);
        Assert.All(segments, segment => Assert.Equal(SegmentStatus.Pending, segment.Status));
        Assert.All(segments, segment => Assert.NotNull(segment.Source));
        Assert.All(segments, segment => Assert.Null(segment.Target));
    }

    [Fact]
    public async Task Load_keeps_the_original_paragraph_ordinal_in_the_path()
    {
        var segments = await Translator.LoadAsync(FixturePath, CancellationToken.None);

        Assert.Equal("/word/document.xml#p0", segments[0].Path);
        Assert.Equal("/word/document.xml#p1", segments[1].Path);
        // 第 7 段只有图片、没被收进列表，但它照样占一个号——否则回写会整体错位一段。
        Assert.Equal("/word/document.xml#p8", segments[7].Path);
        Assert.Equal("/word/header1.xml#p0", segments[8].Path);
    }

    [Fact]
    public async Task WriteBack_preserves_paragraph_properties()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);
        await Translator.WriteBackAsync(input, output, Translated(segments), CancellationToken.None);

        var before = ReadPart(input);
        var after = ReadPart(output);
        Assert.Equal(Count(before, W + "pPr"), Count(after, W + "pPr"));
    }

    [Fact]
    public async Task WriteBack_keeps_image_and_table()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);
        await Translator.WriteBackAsync(input, output, Translated(segments), CancellationToken.None);

        var after = ReadPart(output);
        Assert.Equal(1, Count(after, W + "drawing"));
        Assert.Equal(1, Count(after, W + "tbl"));
    }

    [Fact]
    public async Task WriteBack_output_reopens_as_zip()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);
        await Translator.WriteBackAsync(input, output, Translated(segments), CancellationToken.None);

        // v72: Update 模式不截断旧内容时，写短了的 entry 尾部会残留旧字节，
        // 整个包就打不开了——这条用例是那个坑的护栏。
        using var archive = ZipFile.OpenRead(output);
        using var source = ZipFile.OpenRead(input);
        Assert.Equal(source.Entries.Count, archive.Entries.Count);
    }

    [Fact]
    public async Task WriteBack_does_not_touch_source_file()
    {
        var input = CopyFixture();
        var before = await File.ReadAllBytesAsync(input);
        var segments = await Translator.LoadAsync(input, CancellationToken.None);

        await Translator.WriteBackAsync(input, Path.Combine(_dir, "out.docx"), Translated(segments), CancellationToken.None);

        Assert.Equal(before, await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task WriteBack_replaces_paragraph_text_in_place()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);
        var translated = Translated(segments);

        await Translator.WriteBackAsync(input, output, translated, CancellationToken.None);

        Assert.Equal(translated[0].Target, ParagraphText(ReadPart(output), 0));
        Assert.Equal(translated[1].Target, ParagraphText(ReadPart(output), 1));
    }

    [Fact]
    public async Task WriteBack_marks_a_failed_segment_with_the_source_text()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);
        var failed = segments[0] with { Status = SegmentStatus.Failed };

        await Translator.WriteBackAsync(input, output, [failed], CancellationToken.None);

        Assert.Equal("【未翻译】" + failed.Source, ParagraphText(ReadPart(output), 0));
    }

    [Fact]
    public async Task WriteBack_leaves_skipped_segments_untouched()
    {
        var input = CopyFixture();
        var output = Path.Combine(_dir, "out.docx");
        var segments = await Translator.LoadAsync(input, CancellationToken.None);

        await Translator.WriteBackAsync(input, output, segments, CancellationToken.None);

        // 全是 Pending/Skipped：一个字都不该改。
        Assert.Equal(ParagraphText(ReadPart(input), 0), ParagraphText(ReadPart(output), 0));
    }

    private static IReadOnlyList<DocumentSegment> Translated(IReadOnlyList<DocumentSegment> segments) =>
        [.. segments.Select(segment => segment with { Target = "[en] " + segment.Source, Status = SegmentStatus.Done })];

    private string CopyFixture()
    {
        var path = Path.Combine(_dir, "input.docx");
        File.Copy(FixturePath, path, overwrite: true);
        return path;
    }

    private static XDocument ReadPart(string docxPath)
    {
        using var archive = ZipFile.OpenRead(docxPath);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidOperationException("夹具缺 word/document.xml");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static int Count(XDocument document, XName name) => document.Descendants(name).Count();

    private static string ParagraphText(XDocument document, int ordinal) =>
        string.Concat(document.Descendants(W + "p").ElementAt(ordinal).Descendants(W + "t").Select(text => text.Value));
}
