namespace FastTranslation.Core.Documents;

public enum SegmentStatus
{
    Pending,
    Done,
    Failed,

    /// <summary>没送译：空白段、纯数字/纯符号、单字符。<see cref="DocumentSegment.Target"/> 保持 null。</summary>
    Skipped,
}

/// <summary>
/// 文档里的一段可译文本。
/// <para>
/// <see cref="Path"/> 是「回写时怎么找回这一段」的唯一依据，解析与回写必须用同一套序号规则：
/// 解析时记下 <c>{part}#p{n}</c>（docx）或行号（txt/md），回写时按同一规则重新定位。
/// 不做这一步就只能按数组下标回写，而跳过空白段后下标与 XML 里的段落序数不再一一对应
/// （v72 的第一版就是这么错的，回写会整体错位一段）。
/// </para>
/// </summary>
public sealed record DocumentSegment(int Index, string Path, string Source, string? Target, SegmentStatus Status)
{
    public bool NeedsTranslation => Status is SegmentStatus.Pending;
}
