namespace FastTranslation.Core.Documents;

/// <summary>
/// 「这一段值不值得送译」的唯一判据。txt/md 与 docx 共用同一份实现：两处各写一份判定，
/// 迟早会出现同一种段落在 txt 里被跳过、在 docx 里却被送译的鬼故事。
/// </summary>
internal static class SegmentTextPolicy
{
    /// <summary>
    /// 纯符号/纯数字/单字符段不送译：它们要么是分隔线（<c>---</c>），要么是页码或列表编号，
    /// 送上去只会花钱换回一个被模型顺手改写的版本。判定标准是「有没有一个字母」——
    /// 汉字属于 Letter，所以中英混排都能过；数字与标点单独出现则不算可译内容。
    /// </summary>
    internal static bool ShouldTranslate(string source)
    {
        if (source.Length < 2) return false;
        var normalized = TextNormalization.Normalize(source);
        return TextNormalization.IsMeaningful(normalized) && normalized.Any(char.IsLetter);
    }

    /// <summary>没译成时插在原文前面的标记，用户一眼就能看出哪几段还是原文。</summary>
    internal const string UntranslatedMarker = "【未翻译】";

    /// <summary>
    /// 这一段落盘时该写什么。三态各有归处：译好的写译文，没译成的加标记后保留原文
    /// （静默留下原文会让人以为文档已经全译完了），跳过的不动。
    /// </summary>
    internal static string WriteBackText(DocumentSegment segment) => segment switch
    {
        { Status: SegmentStatus.Done, Target: { Length: > 0 } target } => target,
        { Status: SegmentStatus.Failed } => UntranslatedMarker + segment.Source,
        _ => segment.Source,
    };
}
