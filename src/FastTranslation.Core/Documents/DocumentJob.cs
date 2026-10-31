using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FastTranslation.Core.Documents;

/// <summary>
/// 一份文档翻译作业：解析出的全部段 + 目标语言 + 本地断点文件。
/// <para>
/// 断点只存「已经译好的那段译文」，不存原文：原文还在用户的文档里，抄一份到
/// <c>%LOCALAPPDATA%</c> 等于把用户的正文留在磁盘上多一份，而它的价值只是省一次重译。
/// </para>
/// <para>
/// 断点文件名里带源文件的大小与最后写入时间：用户改了文档，落点就变，
/// 旧断点自然不会被读到——比在文件里记指纹再比对少一套可能写错的逻辑。
/// </para>
/// </summary>
public sealed class DocumentJob
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // 默认实现把中文转义成 \uXXXX，一份中文文档的断点会膨胀到六倍大；
        // 断点只在本地读、不对外传输，宽松编码没有注入面。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>默认落点：<c>%LOCALAPPDATA%\FastTranslation\documents</c>，与 settings.json 同级。</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTranslation", "documents");

    private readonly List<DocumentSegment> _segments;

    public DocumentJob(
        string sourcePath,
        string outputPath,
        string targetLanguage,
        IReadOnlyList<DocumentSegment> segments,
        string? checkpointDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentException.ThrowIfNullOrEmpty(targetLanguage);
        ArgumentNullException.ThrowIfNull(segments);

        SourcePath = sourcePath;
        OutputPath = outputPath;
        TargetLanguage = targetLanguage;
        _segments = [.. segments];
        CheckpointPath = Path.Combine(checkpointDirectory ?? DefaultDirectory, CheckpointName(sourcePath));
    }

    public string SourcePath { get; }

    public string OutputPath { get; }

    public string TargetLanguage { get; }

    /// <summary>段的当前状态。断点回填会就地改写它，所以是只读视图而不是快照。</summary>
    public IReadOnlyList<DocumentSegment> Segments => _segments;

    public string CheckpointPath { get; }

    public int TotalSegments => Segments.Count;

    /// <summary>把当前已译完的段落盘。调用方在每批译完之后调一次，崩掉时最多重译最后一批。</summary>
    public async Task SaveCheckpointAsync(CancellationToken cancellationToken)
    {
        var done = _segments
            .Where(segment => segment is { Status: SegmentStatus.Done, Target: { Length: > 0 } })
            .Select(segment => new CheckpointSegment(segment.Index, segment.Path, segment.Target!))
            .ToList();
        if (done.Count == 0) return;

        var directory = Path.GetDirectoryName(CheckpointPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var payload = new CheckpointPayload(SourcePath, TargetLanguage, TotalSegments, done);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        // 先写临时文件再替换：翻译途中被杀进程时，留下半个 JSON 会让下次启动读断点直接抛。
        var temp = CheckpointPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, CheckpointPath, overwrite: true);
    }

    /// <summary>
    /// 命中且结构一致时把译文回填到对应段，返回是否用上了断点。
    /// 段数或路径序列对不上就整份放弃：错位回填会把 A 段的译文写进 B 段，
    /// 这比重新译一遍糟得多，而重译的代价只是额度。
    /// </summary>
    public async Task<bool> TryRestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CheckpointPath)) return false;
            var json = await File.ReadAllTextAsync(CheckpointPath, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<CheckpointPayload>(json, JsonOptions);
            if (payload is null) return false;
            if (!string.Equals(payload.TargetLanguage, TargetLanguage, StringComparison.Ordinal)) return false;
            if (payload.TotalSegments != TotalSegments || payload.Segments is null) return false;

            var byIndex = _segments.ToDictionary(segment => segment.Index);
            foreach (var entry in payload.Segments)
            {
                if (!byIndex.TryGetValue(entry.I, out var segment)) return false;
                if (!string.Equals(segment.Path, entry.Path, StringComparison.Ordinal)) return false;
            }

            var targets = payload.Segments.ToDictionary(entry => entry.I, entry => entry.T);
            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (targets.TryGetValue(segment.Index, out var target) && !string.IsNullOrEmpty(target))
                {
                    _segments[i] = segment with { Target = target, Status = SegmentStatus.Done };
                }
            }

            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 断点读坏了不该让整份翻译失败：当作没有断点，从头译。
            return false;
        }
    }

    /// <summary>作业成功后删掉断点，别在用户机器上留着上一次的译文。</summary>
    public void DeleteCheckpoint()
    {
        try
        {
            if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
        }
        catch (IOException)
        {
            // 删不掉只是多留一份本地文件，不值得打断已经完成的翻译。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>断点名：源文件路径 + 大小 + 最后写入时间 的 SHA-256。</summary>
    private static string CheckpointName(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var stamp = $"{Path.GetFullPath(sourcePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stamp));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".json";
    }

    private sealed record CheckpointPayload(
        string SourcePath,
        string TargetLanguage,
        int TotalSegments,
        List<CheckpointSegment> Segments);

    private sealed record CheckpointSegment(int I, string Path, string T);
}
