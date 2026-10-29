namespace FastTranslation.Core;

/// <summary>
/// 要识别的截图。只需要 PNG 字节：OCR 模型的默认行为就是「只输出图中文字」，
/// 不传 prompt 也不用指定语言，识别语种由模型自己判断。
/// </summary>
public sealed record ImageOcrRequest(byte[] PngBytes);

/// <summary>
/// OCR 结果。与 <see cref="ImageTranslationResult"/> 的区别是它只带回文本、没有图片，
/// 所以不需要二次下载，一次请求就结束。
/// </summary>
public sealed record ImageOcrResult(
    string Text,
    long ElapsedMs,
    string RemoteUrl)
{
    public TranslationError? Error { get; init; }

    /// <summary>展示层额外的一句说明，例如网关额度用完、这次走了用户自己的 Key。</summary>
    public string? Notice { get; init; }

    public bool Succeeded => Error is null && !string.IsNullOrWhiteSpace(Text);

    public static ImageOcrResult Failed(TranslationError error, long elapsedMs) =>
        new(string.Empty, elapsedMs, string.Empty) { Error = error };
}

public interface IImageOcrProvider : IDisposable
{
    Task<ImageOcrResult> RecognizeAsync(ImageOcrRequest request, CancellationToken cancellationToken);
}
