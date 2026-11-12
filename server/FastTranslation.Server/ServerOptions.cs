namespace FastTranslation.Server;

/// <summary>绑定自 "Server" 配置节（appsettings.json + 环境变量）。</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>SQLite 文件路径。相对路径相对于内容根目录解析。</summary>
    public string DatabasePath { get; set; } = "data/fasttranslation.db";

    /// <summary>Base64（或明文，会给出长度警告）的 HS256 密钥。至少 32 字节。</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>为 false 时注册完全跳过邮箱验证码（本地开发用）。</summary>
    public bool RequireEmailVerification { get; set; }

    public int VerificationCodeMinutes { get; set; } = 10;

    public int VerificationResendSeconds { get; set; } = 60;

    public int VerificationMaxAttempts { get; set; } = 5;

    /// <summary>/api/v1/admin 的共享密钥（通过 X-Admin-Token 请求头发送）。为空则关闭该接口面。</summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>认证类接口的每 IP 每分钟请求预算。</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;

    /// <summary>管理接口的每 IP 每分钟请求预算。0 或负数时回到默认值 20。</summary>
    public int AdminRequestsPerMinute { get; set; } = 20;

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>代理网关，用开发者自己的 DashScope Key 为免费档用户付费。</summary>
    public GatewayOptions Gateway { get; set; } = new();

    /// <summary>个人收款码配置；文件只存在于服务器，绝不进仓库或安装包。</summary>
    public PaymentOptions Payment { get; set; } = new();
}

/// <summary>
/// 订阅订单的收款码。两个路径都可为空——没配置的收款方式不会出现在下单响应里，
/// 两个都没配置时下单接口返回 payment_unavailable，而不是假装能收款。
/// </summary>
public sealed class PaymentOptions
{
    public string WeChatQrPath { get; set; } = string.Empty;

    public string AlipayQrPath { get; set; } = string.Empty;

    /// <summary>单张二维码的硬上限，防止误配成整张截图后每次下单都塞进响应体。</summary>
    public int MaxQrBytes { get; set; } = 200_000;
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}
