namespace FastTranslation.Core;

public sealed record LanguageInfo(string Code, string ApiName, string DisplayName);

/// <summary>
/// Target-language catalog. ApiName is the English name Qwen-MT requires;
/// DisplayName is the zh-CN UI label. Source list per Qwen-MT docs — the full
/// 92-language set must be diffed against the official page before beta.
/// </summary>
public static class LanguageCatalog
{
    public const string ChineseCode = "zh";
    public const string EnglishCode = "en";

    public static readonly IReadOnlyList<LanguageInfo> Languages = new LanguageInfo[]
    {
        new("zh", "Chinese", "中文(简体)"),
        new("en", "English", "英语"),
        new("ja", "Japanese", "日语"),
        new("ko", "Korean", "韩语"),
        new("de", "German", "德语"),
        new("fr", "French", "法语"),
        new("es", "Spanish", "西班牙语"),
        new("pt", "Portuguese", "葡萄牙语"),
        new("ru", "Russian", "俄语"),
        new("it", "Italian", "意大利语"),
        new("ar", "Arabic", "阿拉伯语"),
        new("hi", "Hindi", "印地语"),
        new("th", "Thai", "泰语"),
        new("vi", "Vietnamese", "越南语"),
        new("id", "Indonesian", "印尼语"),
        new("ms", "Malay", "马来语"),
        new("tr", "Turkish", "土耳其语"),
        new("nl", "Dutch", "荷兰语"),
        new("pl", "Polish", "波兰语"),
        new("sv", "Swedish", "瑞典语"),
        new("da", "Danish", "丹麦语"),
        new("fi", "Finnish", "芬兰语"),
        new("no", "Norwegian", "挪威语"),
        new("cs", "Czech", "捷克语"),
        new("sk", "Slovak", "斯洛伐克语"),
        new("hu", "Hungarian", "匈牙利语"),
        new("ro", "Romanian", "罗马尼亚语"),
        new("bg", "Bulgarian", "保加利亚语"),
        new("el", "Greek", "希腊语"),
        new("he", "Hebrew", "希伯来语"),
        new("uk", "Ukrainian", "乌克兰语"),
        new("fa", "Persian", "波斯语"),
        new("ur", "Urdu", "乌尔都语"),
        new("bn", "Bengali", "孟加拉语"),
        new("ta", "Tamil", "泰米尔语"),
        new("te", "Telugu", "泰卢固语"),
        new("ml", "Malayalam", "马拉雅拉姆语"),
        new("kn", "Kannada", "卡纳达语"),
        new("mr", "Marathi", "马拉地语"),
        new("gu", "Gujarati", "古吉拉特语"),
        new("pa", "Punjabi", "旁遮普语"),
        new("my", "Burmese", "缅甸语"),
        new("km", "Khmer", "高棉语"),
        new("lo", "Lao", "老挝语"),
        new("si", "Sinhala", "僧伽罗语"),
        new("ne", "Nepali", "尼泊尔语"),
        new("mn", "Mongolian", "蒙古语"),
        new("kk", "Kazakh", "哈萨克语"),
        new("uz", "Uzbek", "乌兹别克语"),
        new("az", "Azerbaijani", "阿塞拜疆语"),
        new("ka", "Georgian", "格鲁吉亚语"),
        new("hy", "Armenian", "亚美尼亚语"),
        new("sr", "Serbian", "塞尔维亚语"),
        new("hr", "Croatian", "克罗地亚语"),
        new("bs", "Bosnian", "波斯尼亚语"),
        new("sl", "Slovenian", "斯洛文尼亚语"),
        new("lt", "Lithuanian", "立陶宛语"),
        new("lv", "Latvian", "拉脱维亚语"),
        new("et", "Estonian", "爱沙尼亚语"),
        new("sq", "Albanian", "阿尔巴尼亚语"),
        new("mk", "Macedonian", "马其顿语"),
        new("sw", "Swahili", "斯瓦希里语"),
        new("am", "Amharic", "阿姆哈拉语"),
        new("ha", "Hausa", "豪萨语"),
        new("yo", "Yoruba", "约鲁巴语"),
        new("zu", "Zulu", "祖鲁语"),
        new("af", "Afrikaans", "南非荷兰语"),
        new("ca", "Catalan", "加泰罗尼亚语"),
        new("gl", "Galician", "加利西亚语"),
        new("eu", "Basque", "巴斯克语"),
        new("is", "Icelandic", "冰岛语"),
        new("ga", "Irish", "爱尔兰语"),
        new("cy", "Welsh", "威尔士语"),
        new("be", "Belarusian", "白俄罗斯语"),
    };

    private static readonly IReadOnlyDictionary<string, LanguageInfo> ByCode =
        Languages.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string code, out LanguageInfo info)
    {
        info = code is null ? null! : ByCode.GetValueOrDefault(code)!;
        return info is not null;
    }

    /// <summary>Qwen-MT requires the English language name; fall back to the code itself.</summary>
    public static string ToApiName(string code) => TryGet(code, out var info) ? info.ApiName : code;
}
