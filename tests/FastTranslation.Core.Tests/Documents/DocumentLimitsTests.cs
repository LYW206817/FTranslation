using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public class DocumentLimitsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CheckFile_rejects_empty_or_negative_files(long bytes)
    {
        Assert.Equal("文件是空的", DocumentLimits.CheckFile(bytes));
    }

    [Fact]
    public void CheckFile_accepts_the_exact_size_limit()
    {
        Assert.Null(DocumentLimits.CheckFile(DocumentLimits.MaxFileBytes));
    }

    [Fact]
    public void CheckFile_rejects_one_byte_over_the_limit()
    {
        var error = DocumentLimits.CheckFile(DocumentLimits.MaxFileBytes + 1);
        Assert.NotNull(error);
        Assert.Contains("MB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckContent_accepts_the_exact_segment_and_character_limits()
    {
        Assert.Null(DocumentLimits.CheckContent(DocumentLimits.MaxSegments, DocumentLimits.MaxChars));
    }

    [Fact]
    public void CheckContent_rejects_one_segment_over_the_limit()
    {
        var error = DocumentLimits.CheckContent(DocumentLimits.MaxSegments + 1, 0);
        Assert.NotNull(error);
        Assert.Contains("段落太多", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckContent_rejects_one_character_over_the_limit()
    {
        var error = DocumentLimits.CheckContent(DocumentLimits.MaxSegments, DocumentLimits.MaxChars + 1);
        Assert.NotNull(error);
        Assert.Contains("可译字符太多", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckContent_reports_segments_before_characters()
    {
        // 两道上限同时超时，先提示段落数：拆段通常也能把字符数一起降下来。
        var error = DocumentLimits.CheckContent(DocumentLimits.MaxSegments + 1, DocumentLimits.MaxChars + 1);
        Assert.NotNull(error);
        Assert.Contains("段落太多", error, StringComparison.Ordinal);
    }
}
