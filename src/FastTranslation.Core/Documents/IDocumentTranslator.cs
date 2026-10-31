namespace FastTranslation.Core.Documents;

/// <summary>
/// 一种文档格式的解析与回写。实现类必须是无状态的：作业可能被取消后续译，
/// 段的处理状态归 <c>DocumentJob</c> 持有，翻译器只按传入的段集合干活。
/// </summary>
public interface IDocumentTranslator
{
    DocumentFormat Format { get; }

    /// <summary>
    /// 解析出全部段落。必须返回每一段（包括 <see cref="SegmentStatus.Skipped"/> 的），
    /// 因为回写要靠完整的段落列表重建文档。
    /// </summary>
    Task<IReadOnlyList<DocumentSegment>> LoadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 把 <paramref name="segments"/> 的译文写进 <paramref name="outputPath"/>。
    /// 源文件 <paramref name="sourcePath"/> 只读不写：用户的工作文件不该被工具覆盖。
    /// </summary>
    Task WriteBackAsync(string sourcePath, string outputPath, IReadOnlyList<DocumentSegment> segments, CancellationToken cancellationToken);
}
