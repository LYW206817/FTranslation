namespace FastTranslation.Core.Lexicon;

/// <summary>同一词性下的释义组。ECDICT 的释义按行给出词性，同词性的多行合并到这里。</summary>
public sealed record DictionarySense(string PartOfSpeech, IReadOnlyList<string> Definitions);

/// <summary>
/// 本地词库查到的单词卡。ECDICT 不提供例句，所以这里只有音标、词性与释义；
/// 例句是后续按需走通用模型补的，不属于词库本身。
/// </summary>
public sealed record DictionaryEntry(
    string Headword,
    string Phonetic,
    IReadOnlyList<DictionarySense> Senses,
    IReadOnlyList<string> Forms,
    IReadOnlyList<string> Tags)
{
    /// <summary>结果卡状态行上的来源名；与在线模型名区分开，用户能看出没走网络。</summary>
    public const string SourceName = "ECDICT";

    /// <summary>结果卡上当「译文」用的那句：第一个义项的第一条释义。</summary>
    public string Summary => Senses.Count == 0 || Senses[0].Definitions.Count == 0
        ? string.Empty
        : Senses[0].Definitions[0];

    /// <summary>
    /// 单词本详情与复习页共用的多行摘要。和结果卡一样最多 4 组义项、每组 6 条：
    /// 词条的完整释义能到十几条，在详情面板里会顶掉备注和评分按钮。
    /// </summary>
    public string ToDisplayText()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Phonetic)) lines.Add(Phonetic);
        foreach (var sense in Senses.Take(4))
        {
            var prefix = sense.PartOfSpeech.Length == 0 ? "· " : sense.PartOfSpeech + " ";
            lines.Add(prefix + string.Join("；", sense.Definitions.Take(6)));
        }
        if (Forms.Count > 0) lines.Add("变形 " + string.Join(" · ", Forms.Take(4)));
        if (Tags.Count > 0) lines.Add("词表 " + string.Join(" · ", Tags));
        return string.Join("\n", lines);
    }
}

/// <summary>词库读取抽象。查不到时返回 false，调用方回落到在线翻译。</summary>
public interface ILexicon
{
    bool TryLookup(string word, out DictionaryEntry entry);
}
