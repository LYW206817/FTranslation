using System.Threading;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 一次截图翻译交给展示层的全部东西：译图 + 识别到的原文 + 原文的译文。
/// 三段都可能单独缺席（图片翻译失败、OCR 没认出字、译文没回来），所以全部可空。
/// </summary>
internal sealed record ImageCaptureResult(
    byte[]? JpegBytes,
    string SourceText,
    string? TargetText,
    string? TextError,
    string OriginApp,
    string SourceLanguage = "",
    string TargetLanguage = "",
    string Model = "",
    bool PicturePending = false);

/// <summary>
/// 截屏翻译的编排：拖框选区域 → 图片翻译（A）与 OCR（B）并行 → 原文再翻一遍文字（C）→ 交给展示层。
/// 结果去向由两个回调决定（成功给载荷，失败只给消息），所以这里不碰 WPF：
/// App 里分别是 ImageResultWindow 和托盘气泡，测试/换展示方式都不用改这个类。
/// </summary>
internal sealed class ImageTranslationCoordinator : IDisposable
{
    private readonly IImageTranslationProvider _provider;
    private readonly IImageOcrProvider _ocr;
    private readonly TranslationService _translation;
    private readonly Func<AppSettings> _settings;
    private readonly Action<ImageCaptureResult> _present;
    private readonly Action<string> _reportError;
    private readonly Action _waiting;
    private readonly CancellationTokenSource _lifetime = new();

    private int _busy;

    public ImageTranslationCoordinator(
        IImageTranslationProvider provider,
        IImageOcrProvider ocr,
        TranslationService translation,
        Func<AppSettings> settings,
        Action<ImageCaptureResult> present,
        Action<string> reportError,
        Action waiting)
    {
        _provider = provider;
        _ocr = ocr;
        _translation = translation;
        _settings = settings;
        _present = present;
        _reportError = reportError;
        _waiting = waiting;
    }

    /// <summary>
    /// 一次完整流程：拖框 → 翻译 → 展示。用户取消（Esc/右键/框太小）静默返回。
    /// 上一次没结束时直接忽略新请求：两个拖框 overlay 会互相把对方的灰罩截进图片里。
    /// </summary>
    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            WindowsSelectionCapture.Diag("image: ignored, previous run still busy");
            return;
        }
        try
        {
            // origin_app 必须在拖框之前读：拖框 overlay 起来后前台就变成了本程序。
            var originApp = ForegroundInfo.ProcessName();
            WindowsSelectionCapture.Diag("image: pick starting");

            var captured = await ScreenRegionPicker.PickAsync(_lifetime.Token);
            WindowsSelectionCapture.Diag(captured is null
                ? "image: pick cancelled or produced no region"
                : $"image: picked {captured.Width}x{captured.Height} at {captured.Left},{captured.Top}, {captured.PngBytes.Length}B");
            if (captured is null) return;

            // 框选结束后立刻给个等待态：图片翻译 + OCR + 文字翻译三段串起来要好几秒，
            // 期间原来屏幕上什么都没有，用户会以为程序卡死。
            _waiting();

            var settings = _settings();
            var (source, target) = ImageTranslationPolicy.Resolve(settings.SourceLanguage, settings.TargetLanguage);
            // Model 留空：设置页里的 Model 是文本模型，图片翻译要用适配器自己的默认模型
            var request = new ImageTranslationRequest(captured.PngBytes, source, target, string.Empty);

            var started = Environment.TickCount64;
            var imageTask = _provider.TranslateImageAsync(request, _lifetime.Token);
            var ocrTask = _ocr.RecognizeAsync(new ImageOcrRequest(captured.PngBytes), _lifetime.Token);

            // C（把 OCR 出来的原文再翻一遍文字）只依赖 B，所以 B 一回来就启动，不等 A。
            // 实测同一张图：A≈3.3s、B≈0.9s、C≈0.6s；旧顺序先 WhenAll(A,B) 再跑 C 要 5.2s。
            var textTask = TranslateRecognizedAsync(ocrTask, settings);

            // 谁先回来谁先上屏：文字通常 1.5s 就有，译图要 3s 以上，等齐再显示等于白等。
            var shown = await PresentEarlierAsync(imageTask, textTask, originApp).ConfigureAwait(false);

            var image = await imageTask.ConfigureAwait(false);
            var (ocr, text) = await textTask.ConfigureAwait(false);
            var recognized = ocr.Succeeded ? ocr.Text : string.Empty;
            var translated = text is { Succeeded: true } ? text : null;
            var textError = ocr.Succeeded
                ? (text is { Succeeded: false } failed ? failed.Error?.Message : null)
                : ocr.Error?.Message;

            WindowsSelectionCapture.Diag(
                $"image: done at {Environment.TickCount64 - started}ms; image ok={image.Succeeded} err={image.Error?.Code}/{image.Error?.Message}; ocr ok={ocr.Succeeded} err={ocr.Error?.Code}/{ocr.Error?.Message}; text={text?.Succeeded}");

            if (!image.Succeeded && recognized.Length == 0)
            {
                _reportError(image.Error?.Message ?? ocr.Error?.Message ?? "截图翻译失败");
                return;
            }

            if (_lifetime.IsCancellationRequested) return;

            // 先上屏的那段可能已经把同样的内容给过了（文字先到、译图又失败；或译图先到、OCR 没认出字），
            // 再画一遍只会让窗口闪一下，所以只补真正缺的那一半。
            var needsPicture = image.Succeeded && !shown.Image;
            var needsText = recognized.Length > 0 && !shown.Text;
            // 文字已上屏、译图却失败了：图区的转圈还在转，得再画一次把它收掉，否则窗口一直转下去
            var needsSettle = shown.Text && !image.Succeeded;
            if (!needsPicture && !needsText && !needsSettle) return;

            _present(new ImageCaptureResult(
                image.Succeeded ? image.JpegBytes : null,
                recognized,
                translated?.TranslatedText,
                textError,
                originApp,
                translated?.SourceLanguage ?? string.Empty,
                translated?.TargetLanguage ?? string.Empty,
                translated?.Model ?? string.Empty));
        }
        catch (OperationCanceledException)
        {
            // 退出时取消：没有窗口可展示，也不需要报错
        }
        catch (Exception ex)
        {
            // 异常原来只走托盘气泡，气泡不弹时用户和排查者都看不到任何线索。
            WindowsSelectionCapture.Diag($"image: failed {ex.GetType().Name}: {ex.Message}");
            _reportError(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 两段里先回来的那段先上屏。译图和文字各走各的端点，谁快谁慢每次都不一样，
    /// 等齐再显示就是让用户对着转圈看最慢的那条走完。窗口原地换内容，尺寸不变。
    /// </summary>
    /// <returns>已经上屏的是哪一半，调用方据此决定还要不要补一次。</returns>
    private async Task<(bool Image, bool Text)> PresentEarlierAsync(
        Task<ImageTranslationResult> imageTask,
        Task<(ImageOcrResult Ocr, TranslationResult? Text)> textTask,
        string originApp)
    {
        var first = await Task.WhenAny(imageTask, textTask).ConfigureAwait(false);
        if (first == imageTask)
        {
            if (await imageTask.ConfigureAwait(false) is not { Succeeded: true } image) return (false, false);
            // 原文还没来：先给图，识别框留空、保存按钮等文字回来再启用
            _present(new ImageCaptureResult(image.JpegBytes, string.Empty, null, null, originApp));
            return (true, false);
        }

        var (ocr, text) = await textTask.ConfigureAwait(false);
        if (!ocr.Succeeded || ocr.Text.Length == 0) return (false, false);
        var translated = text is { Succeeded: true } ? text : null;
        // 文字赢下 WhenAny 就说明译图还没回：告诉窗口图区继续转圈占位，
        // 否则图区会先收成 0 行、译图回来再撑开，用户看到版面抖一下。
        _present(new ImageCaptureResult(
            null,
            ocr.Text,
            translated?.TranslatedText,
            translated is null ? text?.Error?.Message : null,
            originApp,
            translated?.SourceLanguage ?? string.Empty,
            translated?.TargetLanguage ?? string.Empty,
            translated?.Model ?? string.Empty,
            PicturePending: true));
        return (false, true);
    }

    /// <summary>
    /// OCR 回来就把它翻成文字，不等译图。返回 OCR 结果（失败也要返回，调用方靠它判断有没有原文）
    /// 和文字译文（OCR 没认出字时为 null）。
    /// 语种与模型跟着译文一起交出去：单词本要按它们显示「译自/译成/模型」，漏了就是一条只有中文的孤儿笔记。
    /// </summary>
    private async Task<(ImageOcrResult Ocr, TranslationResult? Text)> TranslateRecognizedAsync(
        Task<ImageOcrResult> ocrTask,
        AppSettings settings)
    {
        var ocr = await ocrTask.ConfigureAwait(false);
        if (!ocr.Succeeded || ocr.Text.Length == 0) return (ocr, null);

        var text = await _translation.TranslateAsync(ocr.Text, settings, _lifetime.Token).ConfigureAwait(false);
        return (ocr, text);
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        _lifetime.Dispose();
    }
}
