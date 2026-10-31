using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests;

public class TextDocumentTranslatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ft-doc-tests-" + Guid.NewGuid().ToString("N"));

    public TextDocumentTranslatorTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("hello.txt", DocumentFormat.Text)]
    [InlineData("hello.md", DocumentFormat.Markdown)]
    [InlineData("HELLO.TXT", DocumentFormat.Text)]
    [InlineData("a.docx", DocumentFormat.Docx)]
    public void Format_comes_from_extension(string name, DocumentFormat expected)
    {
        Assert.True(DocumentFormats.TryFromPath(name, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.doc")]
    [InlineData("noext")]
    [InlineData(null)]
    public void Unsupported_extension_is_rejected(string? name)
    {
        Assert.False(DocumentFormats.TryFromPath(name, out _));
    }

    [Fact]
    public async Task Roundtrips_without_losing_segments()
    {
        var source = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(source, "第一段\n\n第二段\n继续\n\n第三段");

        var translator = new TextDocumentTranslator();
        var segments = await translator.LoadAsync(source, CancellationToken.None);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal(SegmentStatus.Pending, s.Status));
        Assert.Equal("第二段\n继续", segments[1].Source);

        var output = Path.Combine(_dir, "a.out.txt");
        await translator.WriteBackAsync(source, output, segments, CancellationToken.None);

        var again = await translator.LoadAsync(output, CancellationToken.None);
        Assert.Equal(segments.Select(s => s.Source), again.Select(s => s.Source));
    }

    [Fact]
    public async Task Crlf_and_lf_mix_split_the_same_way()
    {
        var source = Path.Combine(_dir, "b.md");
        await File.WriteAllTextAsync(source, "# 标题\r\n\r\n正文一\n\n正文二");

        var segments = await new TextDocumentTranslator(DocumentFormat.Markdown).LoadAsync(source, CancellationToken.None);

        Assert.Equal(["# 标题", "正文一", "正文二"], segments.Select(s => s.Source));
        Assert.Equal([0, 1, 2], segments.Select(s => s.Index));
        Assert.Equal(["0", "1", "2"], segments.Select(s => s.Path));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("123")]
    [InlineData("a")]
    [InlineData("...")]
    public void Non_linguistic_paragraph_is_skipped(string paragraph)
    {
        var segments = TextDocumentTranslator.Split("正文一\n\n" + paragraph);

        Assert.Equal(2, segments.Count);
        Assert.Equal(SegmentStatus.Skipped, segments[1].Status);
        Assert.True(segments[1].NeedsTranslation is false);
    }

    [Fact]
    public async Task Failed_segment_keeps_source_text()
    {
        var source = Path.Combine(_dir, "c.txt");
        await File.WriteAllTextAsync(source, "原文一\n\n原文二");

        var translator = new TextDocumentTranslator();
        var segments = await translator.LoadAsync(source, CancellationToken.None);
        var translated = new[]
        {
            segments[0] with { Target = "one", Status = SegmentStatus.Done },
            segments[1] with { Status = SegmentStatus.Failed },
        };

        var output = Path.Combine(_dir, "c.out.txt");
        await translator.WriteBackAsync(source, output, translated, CancellationToken.None);

        // 失败段保留原文但加标记：静默留下原文会让人以为整份都译完了。
        Assert.Equal("one\n\n【未翻译】原文二\n", await File.ReadAllTextAsync(output));
        Assert.Equal("原文一\n\n原文二", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public void Single_paragraph_without_trailing_newline_is_one_segment()
    {
        var segments = TextDocumentTranslator.Split("只有一段");

        Assert.Single(segments);
        Assert.Equal("只有一段", segments[0].Source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
