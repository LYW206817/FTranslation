namespace FastTranslation.Core;

/// <summary>
/// 图片翻译入参。PngBytes 为原始 PNG 编码字节（DashScope 接受 base64 data URI），
/// SourceLanguage/TargetLanguage 使用 Qwen-MT 的英文语言名（"auto" 表示自动识别）。
/// </summary>
public sealed record ImageTranslationRequest(byte[] PngBytes, string SourceLanguage, string TargetLanguage, string Model);

/// <summary>图片翻译结果。成功时 JpegBytes 为译后图（DashScope 固定返回 JPG，与原图同尺寸）。</summary>
public sealed record ImageTranslationResult(
    byte[] JpegBytes,
    string SourceLanguage,
    string TargetLanguage,
    string Model,
    long ElapsedMs,
    string RemoteUrl)
{
    public TranslationError? Error { get; init; }
    public bool Succeeded => Error is null;
}

public interface IImageTranslationProvider : IDisposable
{
    Task<ImageTranslationResult> TranslateImageAsync(ImageTranslationRequest request, CancellationToken cancellationToken);
}
