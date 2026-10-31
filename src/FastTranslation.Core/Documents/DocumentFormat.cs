namespace FastTranslation.Core.Documents;

/// <summary>文档翻译支持的输入格式。</summary>
public enum DocumentFormat
{
    Docx,
    Text,
    Markdown,
}

public static class DocumentFormats
{
    /// <summary>给用户看的能力说明，扩展名列表只在这里写一次。</summary>
    public const string SupportedExtensions = ".docx / .txt / .md";

    /// <summary>
    /// 按扩展名判定格式。不认识的扩展名返回 false，调用方据此给出「暂不支持 … 格式」提示，
    /// 而不是猜一个解析器去读二进制文件。
    /// </summary>
    public static bool TryFromPath(string? path, out DocumentFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)) { format = DocumentFormat.Docx; return true; }
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) { format = DocumentFormat.Text; return true; }
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) { format = DocumentFormat.Markdown; return true; }
        return false;
    }
}
