using System.IO;

namespace FastTranslation.Server.Orders;

/// <summary>一种可用的收款方式。DataUrl 是 base64 data URL，客户端直接拿来构造图片。</summary>
public sealed record PaymentQr(string Method, string Label, string DataUrl);

/// <summary>
/// 启动时读取服务器上的个人收款码并缓存成 data URL。文件缺失、读不出或超过上限时
/// 只记一条 warning 并跳过——下单接口会根据剩余方式返回 payment_unavailable，
/// 不会因为一张图没放好就整个进程启动失败。
/// </summary>
public sealed class PaymentQrProvider(PaymentOptions options, ILogger<PaymentQrProvider> logger)
{
    private readonly IReadOnlyList<PaymentQr> _methods =
        new[] { Load("wechat", "微信", options.WeChatQrPath, options.MaxQrBytes, logger),
                Load("alipay", "支付宝", options.AlipayQrPath, options.MaxQrBytes, logger) }
            .OfType<PaymentQr>().ToList();

    public IReadOnlyList<PaymentQr> Methods => _methods;

    public bool TryGet(string method, out PaymentQr qr)
    {
        foreach (var item in _methods)
        {
            if (string.Equals(item.Method, method, StringComparison.Ordinal))
            {
                qr = item;
                return true;
            }
        }

        qr = null!;
        return false;
    }

    private static PaymentQr? Load(string method, string label, string path, int maxBytes, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                logger.LogWarning("payment qr {Method} is missing at {Path}", method, full);
                return null;
            }

            var bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0 || bytes.Length > maxBytes)
            {
                logger.LogWarning("payment qr {Method} has invalid size {Bytes} (max {Max})", method, bytes.Length, maxBytes);
                return null;
            }

            var contentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/png",
            };
            return new PaymentQr(method, label, "data:" + contentType + ";base64," + Convert.ToBase64String(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 配置错路径不能让服务起不来；下单时会以 payment_unavailable 收口。
            logger.LogWarning(ex, "payment qr {Method} could not be loaded from {Path}", method, path);
            return null;
        }
    }
}
