using System.Text.Json;

namespace FastTranslation.Core;

public sealed class AppSettings
{
    public string TargetLanguage { get; set; } = "auto";
    public string SourceLanguage { get; set; } = "auto";
    public string Model { get; set; } = "qwen-mt-flash";
    public string Hotkey { get; set; } = "Ctrl+Alt+T";
    public bool ClipboardFallbackEnabled { get; set; } = true;
    public int ClipboardPollMs { get; set; } = 300;
    public bool WriteBackEnabled { get; set; } = true;
    public bool CaptureOnClickOnly { get; set; }
    public List<string> ExcludedProcesses { get; set; } = new();

    public string OverrideBaseUrl { get; set; } = "";
    public bool FirstRunCompleted { get; set; }
    public string Theme { get; set; } = "system";

    public static AppSettings Default() => new();
}

/// <summary>
/// JSON settings in %LOCALAPPDATA%\FastTranslation\settings.json.
/// Any read/deserialize failure resets to defaults (corruption tolerance).
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
/// API key stored via DPAPI (CurrentUser). The provider takes the plaintext key;
/// persistence never leaves the machine unencrypted.
/// Platform note: the DPAPI calls live in Platform.Windows (only allowed NuGet there);
/// Core defines the abstraction and an in-memory default for tests.
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
