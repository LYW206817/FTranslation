using System.Threading;
using FastTranslation.Core;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// 截屏翻译的编排：拖框选区域 → 图片翻译 → 交给展示层。
/// 结果去向由两个回调决定（成功给译图，失败只给消息），所以这里不碰 WPF：
/// App 里分别是 ImageResultWindow 和托盘气泡，测试/换展示方式都不用改这个类。
/// </summary>
internal sealed class ImageTranslationCoordinator : IDisposable
{
    private readonly IImageTranslationProvider _provider;
    private readonly Func<AppSettings> _settings;
    private readonly Action<ImageTranslationResult> _present;
    private readonly Action<string> _reportError;
    private readonly CancellationTokenSource _lifetime = new();

    private int _busy;

    public ImageTranslationCoordinator(
        IImageTranslationProvider provider,
        Func<AppSettings> settings,
        Action<ImageTranslationResult> present,
        Action<string> reportError)
    {
        _provider = provider;
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
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var captured = await ScreenRegionPicker.PickAsync(_lifetime.Token);
            if (captured is null) return;

            var settings = _settings();
            var (source, target) = ImageTranslationPolicy.Resolve(settings.SourceLanguage, settings.TargetLanguage);
            // Model 留空：设置页里的 Model 是文本模型，图片翻译要用适配器自己的默认模型
            var request = new ImageTranslationRequest(captured.PngBytes, source, target, string.Empty);
            var result = await _provider.TranslateImageAsync(request, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;

            if (result.Succeeded) _present(result);
            else _reportError(result.Error?.Message ?? "截图翻译失败");
        }
        catch (OperationCanceledException)
        {
            // 退出时取消：没有窗口可展示，也不需要报错
        }
        catch (Exception ex)
        {
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
