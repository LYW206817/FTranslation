namespace FastTranslation.Core.Lexicon;

/// <summary>
/// ECDICT 的字段整理。词库数据是给程序读的原始格式（缩写词性、逗号堆叠、变位标记），
/// 这里把它变成结果卡能直接渲染的结构；解析放在读取侧而不是构建侧，是为了让这些
/// 规则能被单测钉住——词典数据的边角格式比代码本身更容易出错。
/// </summary>
internal static class EcdictText
{
    private static readonly char[] DefinitionSeparators = [',', '，', ';', '；'];

    private static readonly IReadOnlyDictionary<string, string> TagNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zk"] = "中考",
            ["gk"] = "高考",
            ["cet4"] = "四级",
            ["cet6"] = "六级",
            ["ky"] = "考研",
            ["toefl"] = "托福",
            ["ielts"] = "雅思",
            ["gre"] = "GRE",
        };

    /// <summary>把「n. 银行, 堤, 岸\nvt. 存入银行」这样的多行释义拆成按词性分组的义项。</summary>
    public static IReadOnlyList<DictionarySense> ParseSenses(string translation)
    {
        var groups = new List<SenseGroup>();
        // 上游 CSV 用字面量 "\n" 分行，构建脚本已经还原过；这里再兜一次，
        // 免得换一版词库或手工塞进来的数据把多行释义挤成一条。
        foreach (var rawLine in translation.Replace("\\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var (pos, body) = SplitPartOfSpeech(line);
            var definitions = SplitDefinitions(body);
            if (definitions.Count == 0) continue;

            if (pos.Length == 0)
            {
                // 没有词性前缀的行（[计] 地址 / “run的过去式…”）没有独立分组的意义，
                // 挂到上一个义项后面，否则卡片上会多出一行没有词性的碎片。
                if (groups.Count > 0) AddDistinct(groups[^1].Definitions, definitions);
                else groups.Add(new SenseGroup(string.Empty, definitions));
                continue;
            }

            var existing = groups.Find(g => g.Pos == pos);
            if (existing is not null) AddDistinct(existing.Definitions, definitions);
            else groups.Add(new SenseGroup(pos, definitions));
        }
        return groups.Select(g => new DictionarySense(g.Pos, g.Definitions)).ToArray();
    }

    /// <summary>
    /// exchange 的格式是 "s:banks/i:banking/d:banked/p:banked/3:banks"。
    /// 0/1 是原形与词根，显示出来只是把单词重复一遍，跳过。
    /// </summary>
    public static IReadOnlyList<string> ParseForms(string headword, string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange)) return [];

        var forms = new List<string>();
        foreach (var part in exchange.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0 || colon == part.Length - 1) continue;
            if (part[..colon] is "0" or "1") continue;

            var form = part[(colon + 1)..].Trim();
            if (form.Length == 0 || string.Equals(form, headword, StringComparison.OrdinalIgnoreCase)) continue;
            if (!forms.Contains(form, StringComparer.OrdinalIgnoreCase)) forms.Add(form);
        }
        return forms;
    }

    /// <summary>考试标签转成界面文案；未知标签原样保留，免得新词库丢信息。</summary>
    public static IReadOnlyList<string> ParseTags(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return [];

        var tags = new List<string>();
        foreach (var name in tag.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var display = TagNames.TryGetValue(name, out var known) ? known : name;
            if (!tags.Contains(display, StringComparer.Ordinal)) tags.Add(display);
        }
        return tags;
    }

    /// <summary>
    /// 老词库里混着西里尔字母 ә（U+04D9）和 ǝ（U+01DD）当中央元音，
    /// 字体渲染出来和 ə 不是一回事，统一换掉；没有斜杠的补上，卡片上才像音标。
    /// </summary>
    public static string FormatPhonetic(string phonetic)
    {
        var text = phonetic.Trim().Replace('ә', 'ə').Replace('ǝ', 'ə');
        if (text.Length == 0) return string.Empty;
        return text.StartsWith('/') || text.StartsWith('[') ? text : "/" + text + "/";
    }

    private static (string Pos, string Body) SplitPartOfSpeech(string line)
    {
        // 词性前缀只会出现在行首几个字符里；再往后出现的句点属于释义本身。
        var dot = line.IndexOf('.');
        if (dot is <= 0 or > 5) return (string.Empty, line);

        var prefix = line[..dot];
        if (!prefix.All(char.IsAsciiLetter)) return (string.Empty, line);

        var pos = MapPartOfSpeech(prefix);
        return pos is null ? (string.Empty, line) : (pos, line[(dot + 1)..].Trim());
    }

    private static string? MapPartOfSpeech(string prefix) => prefix.ToLowerInvariant() switch
    {
        "n" or "pl" => "n.",
        "v" or "vt" or "vi" or "aux" => "v.",
        "a" or "adj" => "adj.",
        "ad" or "adv" => "adv.",
        "prep" => "prep.",
        "conj" => "conj.",
        "pron" => "pron.",
        "num" => "num.",
        "art" => "art.",
        "int" or "interj" => "int.",
        _ => null,
    };

    private static List<string> SplitDefinitions(string body)
    {
        var definitions = new List<string>();
        foreach (var part in body.Split(DefinitionSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!definitions.Contains(part, StringComparer.Ordinal)) definitions.Add(part);
        }
        return definitions;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> additions)
    {
        foreach (var item in additions)
        {
            if (!target.Contains(item, StringComparer.Ordinal)) target.Add(item);
        }
    }

    private sealed class SenseGroup(string pos, List<string> definitions)
    {
        public string Pos { get; } = pos;
        public List<string> Definitions { get; } = definitions;
    }
}
