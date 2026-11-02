using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

/// <summary>
/// v68：App.xaml 里的 DynamicResource 找不到键不会报错，只会静默取默认值。
/// PrimaryButton 的禁用态就因此引用了一个从未定义的 HairStrongBrush,结果是
/// 「蓝底灰字」的禁用按钮。这条测试把「引用了就必须有人定义」钉下来。
/// </summary>
public class ThemeResourceKeysTests
{
    /// <summary>
    /// ThemeBridge 用 `SetGrade` 循环生成的行，键名在运行时才拼出来，
    /// 静态扫不出，这里手写一份。
    /// </summary>
    private static readonly string[] GeneratedKeys =
        new[] { "Known", "Familiar", "Fuzzy", "Forgot" }
            .SelectMany(g => new[]
            {
                $"Grade{g}Brush", $"Grade{g}FillBrush", $"Grade{g}BorderBrush", $"Grade{g}HoverBrush",
            })
            .ToArray();

    [Fact]
    public void EveryDynamicResourceKeyInAppXamlIsDeclaredOrInjected()
    {
        var root = RepositoryRoot();
        var xamlFiles = Directory.GetFiles(
            Path.Combine(root, "src", "FastTranslation.App"), "*.xaml", SearchOption.AllDirectories);
        var bridge = File.ReadAllText(Path.Combine(root, "src", "FastTranslation.App", "ThemeBridge.cs"));

        var declared = xamlFiles
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "x:Key=\"([A-Za-z0-9]+)\"")
                .Select(m => m.Groups[1].Value));
        // v85：雾光布局往资源里塞了三支 DropShadowEffect（CardShadowEffect 等），
        // 它们和画刷一样只在 ThemeBridge 里出现，所以键名后缀也要认 Effect。
        var injected = Regex.Matches(bridge, "\"([A-Za-z0-9]+(?:Brush|Effect))\"")
            .Select(m => m.Groups[1].Value)
            .Concat(GeneratedKeys);
        var known = declared.Concat(injected).ToHashSet(StringComparer.Ordinal);

        var referenced = xamlFiles
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "\\{DynamicResource ([A-Za-z0-9]+)\\}")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(key => !known.Contains(key))
            .ToArray();

        Assert.Empty(referenced);
    }

    /// <summary>从测试输出目录往上找到仓库根(含 FastTranslation.slnx 的那层)。</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FastTranslation.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
