using System.Text;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

Console.OutputEncoding = Encoding.UTF8;

var key = Environment.GetEnvironmentVariable("QIANWEN_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.WriteLine("FAIL: QIANWEN_API_KEY not set");
    return 1;
}
Console.WriteLine($"key present ({key.Length} chars)");

// 1) real translation through the product provider, both directions
using (var provider = new QwenMtProvider(() => key))
{
    var en2zh = await provider.TranslateAsync(
        new TranslationRequest("The quick brown fox jumps over the lazy dog.", "auto", "Chinese", "qwen-mt-flash"),
        CancellationToken.None);
    Report("en->zh", en2zh);

    var zh2en = await provider.TranslateAsync(
        new TranslationRequest("划词翻译工具需要对低内存场景做优化。", "auto", "English", "qwen-mt-flash"),
        CancellationToken.None);
    Report("zh->en", zh2en);

    var longText = string.Join(" ", Enumerable.Repeat("This sentence is part of a longer stress input for the translation pipeline. ", 24));
    var longResult = await provider.TranslateAsync(
        new TranslationRequest(longText.Trim(), "auto", "Chinese", "qwen-mt-flash"),
        CancellationToken.None);
    Report($"long({longText.Trim().Length} chars)", longResult);
}

// 2) bad key must classify as Authentication
using (var badProvider = new QwenMtProvider(() => "sk-invalid-key-for-negative-test"))
{
    var bad = await badProvider.TranslateAsync(
        new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"), CancellationToken.None);
    Console.WriteLine($"bad-key -> {(bad.Succeeded ? "UNEXPECTED SUCCESS" : $"error={bad.Error?.Code}")}");
}

// 3) persist the real key via DPAPI and mark onboarding done for the morning smoke
var secrets = new DpapiSecretStore();
secrets.SaveSecret(QwenMtProvider.ApiKeySecretName, key);
var settingsService = new SettingsService();
var settings = settingsService.Load();
settings.FirstRunCompleted = true;
settingsService.Save(settings);
Console.WriteLine("key stored via DPAPI; FirstRunCompleted=true");

// verify roundtrip through the store
var reloaded = new DpapiSecretStore().LoadSecret(QwenMtProvider.ApiKeySecretName);
Console.WriteLine($"dpapi roundtrip ok={(reloaded == key)}");

static void Report(string label, TranslationResult r) =>
    Console.WriteLine(r.Succeeded
        ? $"{label}: OK {r.ElapsedMs}ms | {r.TranslatedText[..Math.Min(80, r.TranslatedText.Length)]}"
        : $"{label}: FAIL {r.Error?.Code} {r.Error?.Message}");

return 0;
