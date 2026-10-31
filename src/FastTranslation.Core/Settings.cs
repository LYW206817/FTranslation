using System.Text.Json;

namespace FastTranslation.Core;

public sealed class AppSettings
{
    public string TargetLanguage { get; set; } = "auto";
    public string SourceLanguage { get; set; } = "auto";
    public string Model { get; set; } = "qwen-mt-flash";
    public string Hotkey { get; set; } = "Ctrl+Alt+T";

    // 截图翻译单独一个热键：取词热键保持不变，两个入口互不影响
    public string ImageHotkey { get; set; } = "Ctrl+Alt+S";
    public bool ClipboardFallbackEnabled { get; set; } = true;
    public int ClipboardPollMs { get; set; } = 300;
    public bool WriteBackEnabled { get; set; } = true;
    public bool CaptureOnClickOnly { get; set; }
    public List<string> ExcludedProcesses { get; set; } = new();

    public string OverrideBaseUrl { get; set; } = "";

    // 账号服务（FastTranslation.Server）。留空 = 内置默认主机；做成设置项而不是常量，
    // 这样测试服务器或自建部署都不必重新编译。
    public string AccountBaseUrl { get; set; } = "";
    public bool FirstRunCompleted { get; set; }
    public string Theme { get; set; } = "system";

    // 文档翻译记住上次的目标语言与目录：一份文档翻完常要接着翻下几份，
    // 每次重选语言和目录是纯粹的重复劳动。缺字段时 JSON 反序列化直接用默认值，无需迁移。
    public string DocumentTargetLanguage { get; set; } = "en";
    public string DocumentLastFolder { get; set; } = "";

    public static AppSettings Default() => new();
}

/// <summary>
/// %LOCALAPPDATA%\FastTranslation\settings.json 里的 JSON 设置。
/// 任何读取/反序列化失败都回落到默认值（容忍损坏）。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _filePath;

    public SettingsService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation");
        _filePath = Path.Combine(_directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return AppSettings.Default();
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? AppSettings.Default();
        }
        catch
        {
            return AppSettings.Default();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}

/// <summary>
/// 用 DPAPI（CurrentUser）保存的 API Key。provider 拿到的是明文 Key；
/// 落盘时永远不会以未加密形式离开本机。
/// 平台说明：DPAPI 调用在 Platform.Windows（只有那里允许引 NuGet）；
/// Core 只定义抽象与测试用的内存实现。
/// </summary>
public interface ISecretStore
{
    void SaveSecret(string name, string value);
    string? LoadSecret(string name);
    void DeleteSecret(string name);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new();
    public void SaveSecret(string name, string value) => _secrets[name] = value;
    public string? LoadSecret(string name) => _secrets.GetValueOrDefault(name);
    public void DeleteSecret(string name) => _secrets.Remove(name);
}