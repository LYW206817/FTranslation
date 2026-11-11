using System.Globalization;

namespace FastTranslation.Core.Documents;

/// <summary>一次文档翻译的结果。<see cref="FailedSegments"/> 大于 0 时译文里会有「【未翻译】」标记。</summary>
public sealed record DocumentJobResult(string OutputPath, int TotalSegments, int FailedSegments, long Chars)
{
    /// <summary>
    /// 没译成的段，按原文顺序（v72）。界面靠它列出「哪几段没译」并让用户重试；
    /// 只带段路径与原文，不回传译文，所以这份清单不会额外占多少内存。
    /// </summary>
    public IReadOnlyList<DocumentSegment> Failed { get; init; } = [];
}

/// <summary>选中文件之后的预检结果：段数与预计要花的字符数（v72）。</summary>
public sealed record DocumentPreview(DocumentFormat Format, int TotalSegments, long Chars);

/// <summary>
/// 文档翻译的编排：解析 → 断点回填 → 预检额度 → 逐段翻译 → 回写 → 清理断点。
/// <para>
/// 不碰 WPF，展示全靠三个回调（成功载荷 / 错误文案 / 进度），这样它既能被单测直接驱动，
/// 也能在界面之外（比如将来的命令行）复用。
/// </para>
/// </summary>
public sealed class DocumentTranslationCoordinator
{
    private readonly Func<DocumentFormat, IDocumentTranslator> _translatorFactory;
    private readonly Func<string, string, CancellationToken, Task<string>> _translateText;
    private readonly Func<string?> _outputDirectory;
    private readonly Action<DocumentJobResult> _present;
    private readonly Action<string> _reportError;
    private readonly Action<double, int, int> _progress;
    private readonly string? _checkpointDirectory;

    /// <param name="checkpointDirectory">
    /// 断点落点，null 用 <see cref="DocumentJob.DefaultDirectory"/>。做成可注入是为了测试不去碰用户的
    /// <c>%LOCALAPPDATA%</c>——测试往真实目录写断点会污染下一个跑测试的人。
    /// </param>
    public DocumentTranslationCoordinator(
        Func<DocumentFormat, IDocumentTranslator> translatorFactory,
        Func<string, string, CancellationToken, Task<string>> translateText,
        Func<string?> outputDirectory,
        Action<DocumentJobResult> present,
        Action<string> reportError,
        Action<double, int, int> progress,
        string? checkpointDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(translatorFactory);
        ArgumentNullException.ThrowIfNull(translateText);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(reportError);
        ArgumentNullException.ThrowIfNull(progress);

        _translatorFactory = translatorFactory;
        _translateText = translateText;
        _outputDirectory = outputDirectory;
        _present = present;
        _reportError = reportError;
        _progress = progress;
        _checkpointDirectory = checkpointDirectory;
    }

    /// <summary>
    /// 翻译一份文档。<paramref name="remainingChars"/> 是这个账号还能用的字符额度，
    /// 传 null 表示不预检（例如用自带 Key 直连、额度不受我们限制）。
    /// </summary>
    public async Task RunAsync(
        string sourcePath,
        string targetLanguage,
        int? remainingChars,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(targetLanguage);
        try
        {
            await RunCoreAsync(sourcePath, targetLanguage, remainingChars, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消：不是错误，界面上由调用方把状态还原。
        }
        catch (QuotaExhaustedException ex)
        {
            // 额度耗尽会让整份作业停下来，所以文案要说清「已经译好的部分还在」，
            // 否则用户会以为白等了——断点已经写盘，换 Key 或续费后重开就接着译。
            _reportError(ex.Message + "。已译好的段落已保存在断点里，额度恢复后再对同一份文件点开始会接着译。");
        }
        catch (Exception ex)
        {
            _reportError(ex.Message);
        }
    }

    private async Task RunCoreAsync(
        string sourcePath,
        string targetLanguage,
        int? remainingChars,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (loaded is null) return;

        var (_, translator, segments, chars) = loaded.Value;
        var outputPath = BuildOutputPath(sourcePath, targetLanguage);
        var job = new DocumentJob(sourcePath, outputPath, targetLanguage, segments, _checkpointDirectory);
        await job.TryRestoreCheckpointAsync(cancellationToken).ConfigureAwait(false);

        // v127: 预检按「回填断点后真正还要译的段」算。原来先按整份文件预检、再读断点，
        // 续译时会把已经译好（且已经付过额度）的段再算一遍，剩余额度不够就整份拒绝。
        // 预检仍必须在开始翻译之前：译到 80% 才发现额度不够，用户既拿不到完整文档，
        // 已经花掉的额度也退不回来。
        var pendingChars = CountChars(job.Segments);
        if (remainingChars is { } remaining && pendingChars > remaining)
        {
            _reportError(string.Format(
                CultureInfo.InvariantCulture,
                "本月剩余额度只够 {0:N0} 字符，本次还需译 {1:N0} 字符",
                remaining,
                pendingChars));
            return;
        }

        var total = job.TotalSegments;
        var translatable = job.Segments.Count(segment => segment.Status != SegmentStatus.Skipped);
        var pending = job.Segments.Count(segment => segment.NeedsTranslation);
        var alreadyDone = translatable - pending;
        var translate = BuildTranslate(targetLanguage);
        // 进度直接回调，不走 Progress<T>：Progress 会投递到捕获的同步上下文，
        // 在 WPF 里就等于「每译完一段切一次 UI 线程」，而回调方自己会切。
        // 分母用「可译段」而不是总段：Skipped 段永远不会完成，否则进度到不了 100%；
        // 分子加上断点里已译好的段，续译才不会从 0 开始。
        var progress = new InlineProgress(done =>
            _progress(
                translatable == 0 ? 1 : Math.Clamp((double)(alreadyDone + done) / translatable, 0, 1),
                alreadyDone + done,
                translatable));
        _progress(0, alreadyDone, translatable);

        var partial = new Dictionary<int, DocumentSegment>();
        var partialGate = new object();
        using var saveGate = new SemaphoreSlim(1, 1);
        long lastSavedAt = 0;
        const int PartialSaveIntervalMs = 10_000;

        var translated = await SegmentTranslator.TranslateAsync(
                job.Segments, translate, progress, cancellationToken, SavePartialAsync)
            .ConfigureAwait(false);
        var finished = new DocumentJob(sourcePath, outputPath, targetLanguage, translated, _checkpointDirectory);

        await translator.WriteBackAsync(sourcePath, outputPath, translated, cancellationToken).ConfigureAwait(false);
        // 还有失败段时保留断点：点「重试」要只补译失败段，不能把已经扣过额度的段全部重译。
        // 全部成功才删，别在用户机器上留着译文副本。
        var failed = translated.Where(segment => segment.Status == SegmentStatus.Failed).ToList();
        if (failed.Count == 0)
        {
            finished.DeleteCheckpoint();
        }
        else
        {
            await finished.SaveCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // 未译段的明细随结果一起交出去：回写之后界面才知道该给用户列哪几段去重试。
        // 译文本身不回传——它已经落在 outputPath 里了，再拷一遍只是白占内存。
        _present(new DocumentJobResult(outputPath, total, failed.Count, chars) { Failed = failed });

        // 翻译途中取消或额度中断时把已完成的单元落盘。逐批写整个断点会让一份大文档产生
        // 几十倍 IO，所以最多 10 秒落一次；这个窗口内丢的进度最多重译十几秒。
        async Task SavePartialAsync(IReadOnlyList<DocumentSegment> done)
        {
            lock (partialGate)
            {
                foreach (var segment in done) partial[segment.Index] = segment;
            }
            if (Environment.TickCount64 - Volatile.Read(ref lastSavedAt) < PartialSaveIntervalMs) return;
            if (!await saveGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (Environment.TickCount64 - Volatile.Read(ref lastSavedAt) < PartialSaveIntervalMs) return;
                DocumentSegment[] snapshot;
                lock (partialGate)
                {
                    snapshot = [.. job.Segments.Select(segment =>
                        partial.TryGetValue(segment.Index, out var translatedSegment) ? translatedSegment : segment)];
                }
                var partialJob = new DocumentJob(sourcePath, outputPath, targetLanguage, snapshot, _checkpointDirectory);
                await partialJob.SaveCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref lastSavedAt, Environment.TickCount64);
            }
            catch (Exception)
            {
                // 断点写盘失败只影响续译能力，不能把正在进行的翻译打断
            }
            finally
            {
                saveGate.Release();
            }
        }
    }

    /// <summary>
    /// 只解析不翻译：界面在用户选中文件后拿它显示「N 段 / 预计消耗 X 字符」，
    /// 与其后的 <see cref="RunAsync"/> 共用同一条解析与校验路径，两处口径不可能走偏。
    /// 校验不通过时返回 null 并已经报过错误文案。
    /// </summary>
    public async Task<DocumentPreview?> PreviewAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        try
        {
            var loaded = await LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (loaded is null) return null;

            var (format, _, segments, chars) = loaded.Value;
            return new DocumentPreview(format, segments.Count, chars);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _reportError(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 格式 → 大小 → 解析 → 内容上限，四道校验走完才把段交出去。
    /// 解析一次就把段与字符数一起带回来：只为显示预检再解析一遍，200 页 docx 要多花一秒。
    /// </summary>
    private async Task<LoadedDocument?> LoadAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!DocumentFormats.TryFromPath(sourcePath, out var format))
        {
            _reportError($"不支持的格式，当前可以翻译 {DocumentFormats.SupportedExtensions}");
            return null;
        }

        var fileError = DocumentLimits.CheckFile(new FileInfo(sourcePath).Length);
        if (fileError is not null)
        {
            _reportError(fileError);
            return null;
        }

        var translator = _translatorFactory(format);
        var segments = await translator.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var chars = CountChars(segments);
        var contentError = DocumentLimits.CheckContent(segments.Count, chars)
            ?? DocumentLimits.CheckSegments(segments);
        if (contentError is not null)
        {
            _reportError(contentError);
            return null;
        }

        return new LoadedDocument(format, translator, segments, chars);
    }

    /// <summary>解析结果三元组。<see cref="Chars"/> 是预计要花的额度，只算真正会送上去的段。</summary>
    private readonly record struct LoadedDocument(
        DocumentFormat Format,
        IDocumentTranslator Translator,
        IReadOnlyList<DocumentSegment> Segments,
        long Chars);

    private Func<string, CancellationToken, Task<string>> BuildTranslate(string targetLanguage) =>
        (text, token) => _translateText(text, targetLanguage, token);

    /// <summary>没译成的段与跳过（空白/纯数字）的段都不计额度：服务端也只按真正送上去的字数记账。</summary>
    private static long CountChars(IReadOnlyList<DocumentSegment> segments) =>
        segments.Where(segment => segment.NeedsTranslation).Sum(segment => (long)segment.Source.Length);

    /// <summary>
    /// 输出文件放用户选定的目录（默认源文件旁边），名字加语言后缀。
    /// 绝不覆盖源文件：翻译中途出错时用户至少还有原件，而「原件被半成品覆盖」是不可恢复的。
    /// </summary>
    private string BuildOutputPath(string sourcePath, string targetLanguage)
    {
        var directory = _outputDirectory() ?? Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        return Path.Combine(directory, $"{name}.{targetLanguage}{extension}");
    }

    /// <summary>同步转发的进度实现：回调方负责切线程，这里不再套一层调度。</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
