using System.Text.RegularExpressions;

namespace FastTranslation.Core;

/// <summary>本地源语言检测结果。Code 为语言码（未知时 "auto"）；IsConfident 表示是否足以对外显示。</summary>
public sealed record LanguageDetection(string Code, bool IsConfident);

/// <summary>
/// 轻量本地源语言检测，只用于结果卡显示「从什么翻译」，不参与翻译请求。
/// Qwen-MT 不回报检测到的源语言，auto 模式下接口里的 source 只是猜测，直接显示会误导
/// （葡萄牙语会被标成英语），故在本地用 Unicode 脚本 + 拉丁停用词打分推断。
/// 脚本对非拉丁语言可靠；拉丁语言靠特征词，短文本置信不足时回落「自动检测」，宁缺勿错。
/// </summary>
public static class SourceLanguageDetector
{
    private const string Auto = "auto";
    private const string AutoLabel = "自动检测";
    private const int MinConfidentHits = 2;
    private const int MinConfidentMargin = 1;

    // 假名优先于汉字：日语混用两者，先命中假名才能判 ja 而非 zh。
    private static readonly (string Code, Regex Pattern)[] Scripts =
    [
        ("ja", new Regex("[\\u3040-\\u30FF]")),
        ("ko", new Regex("[\\uAC00-\\uD7AF]")),
        ("zh", new Regex("[\\u4E00-\\u9FFF\\u3400-\\u4DBF]")),
        ("ru", new Regex("[\\u0400-\\u04FF]")),
        ("ar", new Regex("[\\u0600-\\u06FF]")),
        ("th", new Regex("[\\u0E00-\\u0E7F]")),
        ("hi", new Regex("[\\u0900-\\u097F]")),
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> Stopwords =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["en"] = Set("the is are was were to of and in that it for on with as be at by this have from or an not can how what you over"),
            ["pt"] = Set("o a os as de do da dos das em um uma para com não que e é como posso oi você ajudar por no na ao"),
            ["es"] = Set("el la los las de del en un una para con no que y es como puedo hola ayudar por lo le su"),
            ["fr"] = Set("le la les de des du un une et est ne pas que je tu il nous vous comment puis bonjour aider pour ce"),
            ["de"] = Set("der die das und ist nicht ein eine zu mit für auf wie kann ich ihnen hallo helfen den dem"),
            ["it"] = Set("il lo la i gli le di del della che è non come posso ciao aiutare per un una e"),
        };

    private static readonly Regex LatinToken = new("[a-z\\u00E0-\\u00FF']+", RegexOptions.Compiled);

    /// <summary>推断文本源语言；脚本命中即高置信，拉丁靠停用词打分，置信不足返回 auto。</summary>
    public static LanguageDetection Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new LanguageDetection(Auto, false);
        foreach (var (code, pattern) in Scripts)
            if (pattern.IsMatch(text)) return new LanguageDetection(code, true);

        var tokens = LatinToken.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();
        if (tokens.Length == 0) return new LanguageDetection(Auto, false);

        var scores = Stopwords
            .Select(kv => (kv.Key, Hits: tokens.Count(t => kv.Value.Contains(t))))
            .OrderByDescending(s => s.Hits)
            .ToArray();
        var top = scores[0];
        if (top.Hits == 0) return new LanguageDetection(Auto, false);
        // 至少两个特征词且领先第二名，才敢对外显示；单词级文本不猜。
        var confident = top.Hits >= MinConfidentHits && top.Hits - scores[1].Hits >= MinConfidentMargin;
        return new LanguageDetection(top.Key, confident);
    }

    /// <summary>结果卡源语言标签：手动指定用指定值；auto 时高置信才显示检测名，否则「自动检测」。</summary>
    public static string DisplaySource(string sourcePreference, string text)
    {
        if (!string.Equals(sourcePreference, Auto, StringComparison.OrdinalIgnoreCase)
            && LanguageCatalog.TryGet(sourcePreference, out var chosen))
            return chosen.DisplayName;
        var detection = Detect(text);
        return detection.IsConfident && LanguageCatalog.TryGet(detection.Code, out var detected)
            ? detected.DisplayName
            : AutoLabel;
    }

    private static HashSet<string> Set(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}