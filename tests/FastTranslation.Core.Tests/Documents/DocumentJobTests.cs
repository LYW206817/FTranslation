using FastTranslation.Core.Documents;
using Xunit;

namespace FastTranslation.Core.Tests.Documents;

public sealed class DocumentJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ft-tests-" + Guid.NewGuid().ToString("N"));

    public DocumentJobTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Checkpoint_roundtrips_completed_segments()
    {
        var source = WriteSource("roundtrip.docx");
        var done = DoneAt(NewJob(source), 0, "译好的第一段");
        done = DoneAt(done, 2, "译好的第三段");
        await done.SaveCheckpointAsync(CancellationToken.None);

        // 新作业重新解析后状态是 Pending，靠断点把译文填回来。
        var second = NewJob(source);
        var restored = await second.TryRestoreCheckpointAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal(SegmentStatus.Done, second.Segments[0].Status);
        Assert.Equal("译好的第一段", second.Segments[0].Target);
        Assert.Equal(SegmentStatus.Done, second.Segments[2].Status);
        Assert.Equal(SegmentStatus.Pending, second.Segments[1].Status);
    }

    [Fact]
    public async Task Checkpoint_is_ignored_when_file_changed()
    {
        var source = WriteSource("changed.docx");
        var job = DoneAt(NewJob(source), 0, "旧译文");
        await job.SaveCheckpointAsync(CancellationToken.None);

        // 断点名里带 LastWriteTimeUtc：改了文件就该落在一个新的断点上。
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));
        var changed = NewJob(source);

        Assert.False(await changed.TryRestoreCheckpointAsync(CancellationToken.None));
        Assert.Equal(SegmentStatus.Pending, changed.Segments[0].Status);
    }

    [Fact]
    public async Task Checkpoint_is_ignored_for_a_different_target_language()
    {
        var source = WriteSource("lang.docx");
        var job = DoneAt(NewJob(source, "en"), 0, "english");
        await job.SaveCheckpointAsync(CancellationToken.None);

        // 同一份文档换目标语言：断点是按语言隔离的，不能把英文译文当日文用。
        var japanese = NewJob(source, "ja");

        Assert.False(await japanese.TryRestoreCheckpointAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoint_is_deleted_after_success()
    {
        var source = WriteSource("cleanup.docx");
        var job = DoneAt(NewJob(source), 0, "译文");
        await job.SaveCheckpointAsync(CancellationToken.None);
        Assert.True(File.Exists(job.CheckpointPath));

        job.DeleteCheckpoint();

        Assert.False(File.Exists(job.CheckpointPath));
    }

    [Fact]
    public async Task Checkpoint_does_not_store_source_text()
    {
        var source = WriteSource("privacy.docx");
        var job = DoneAt(NewJob(source), 0, "译好的第一段");
        await job.SaveCheckpointAsync(CancellationToken.None);

        // 断点文件必须只含 {i,path,t}：原文还在用户的文档里，抄一份到磁盘等于多留一处正文。
        var json = await File.ReadAllTextAsync(job.CheckpointPath);

        Assert.Contains("译好的第一段", json, StringComparison.Ordinal);
        Assert.DoesNotContain("第一段原文", json, StringComparison.Ordinal);
    }

    private static DocumentJob DoneAt(DocumentJob job, int index, string target)
    {
        var list = job.Segments.ToList();
        list[index] = list[index] with { Target = target, Status = SegmentStatus.Done };
        return new DocumentJob(job.SourcePath, job.OutputPath, job.TargetLanguage, list, Path.GetDirectoryName(job.CheckpointPath));
    }

    private DocumentJob NewJob(string sourcePath, string targetLanguage = "en") =>
        new(sourcePath, Path.Combine(_dir, "out-" + Path.GetFileName(sourcePath)), targetLanguage, Segments(), _dir);

    private static IReadOnlyList<DocumentSegment> Segments() =>
    [
        new(0, "/word/document.xml#p0", "第一段原文", null, SegmentStatus.Pending),
        new(1, "/word/document.xml#p1", "第二段原文", null, SegmentStatus.Pending),
        new(2, "/word/document.xml#p2", "第三段原文", null, SegmentStatus.Pending),
    ];

    private string WriteSource(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not a real docx, but the checkpoint only needs a stable location");
        return path;
    }
}
