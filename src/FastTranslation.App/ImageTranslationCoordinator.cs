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
    string OriginApp);

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
    private readonly CancellationTokenSource _lifetime = new();

    private int _busy;

    public ImageTranslationCoordinator(
        IImageTranslationProvider provider,
        IImageOcrProvider ocr,
        TranslationService translation,
        Func<AppSettings> settings,
        Action<ImageCaptureResult> present,
        Action<string> reportError)
    {
        _provider = provider;
        _ocr = ocr;
        _translation = translation;
        _settings = settings;
        _present = present;
        _reportError = reportError;
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

            var settings = _settings();
            var (source, target) = ImageTranslationPolicy.Resolve(settings.SourceLanguage, settings.TargetLanguage);
            // Model 留空：设置页里的 Model 是文本模型，图片翻译要用适配器自己的默认模型
            var request = new ImageTranslationRequest(captured.PngBytes, source, target, string.Empty);

            // A（图片翻译）与 B（OCR）并行：用户等的是两者较慢的那个，不是两者之和。
            var imageTask = _provider.TranslateImageAsync(request, _lifetime.Token);
            var ocrTask = _ocr.RecognizeAsync(new ImageOcrRequest(captured.PngBytes), _lifetime.Token);
            await Task.WhenAll(imageTask, ocrTask).ConfigureAwait(false);

            var image = imageTask.Result;
            var ocr = ocrTask.Result;
            WindowsSelectionCapture.Diag(
                $"image: translate ok={image.Succeeded} err={image.Error?.Code}/{image.Error?.Message}; ocr ok={ocr.Succeeded} err={ocr.Error?.Code}/{ocr.Error?.Message}");

            if (!image.Succeeded && !ocr.Succeeded)
            {
                _reportError(image.Error?.Message ?? ocr.Error?.Message ?? "截图翻译失败");
                return;
            }

            var recognized = ocr.Succeeded ? ocr.Text : string.Empty;
            string? textError = ocr.Succeeded ? null : ocr.Error?.Message;

            // C：把识别出的原文再过一遍文字翻译。图片翻译给的是「译图」，用户想存单词本还缺一句译文。
            string? translated = null;
            if (recognized.Length > 0)
            {
                var translatedResult = await _translation.TranslateAsync(recognized, settings, _lifetime.Token).ConfigureAwait(false);
                if (translatedResult.Succeeded) translated = translatedResult.TranslatedText;
                else textError = translatedResult.Error?.Message;
            }

            if (_lifetime.IsCancellationRequested) return;

            _present(new ImageCaptureResult(
                image.Succeeded ? image.JpegBytes : null,
                recognized,
                translated,
                textError,
                originApp));
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

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        _lifetime.Dispose();
    }
}
