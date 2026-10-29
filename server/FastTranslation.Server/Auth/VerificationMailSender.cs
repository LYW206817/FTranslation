using System.Net;
using System.Net.Mail;

namespace FastTranslation.Server.Auth;

public interface IVerificationMailSender
{
    Task SendCodeAsync(string email, string code, CancellationToken ct);
}

/// <summary>
/// 已配置时走 SMTP 投递；否则把验证码写进日志，让开发者
/// 在没有邮件服务器的情况下也能在本地完成注册。
/// </summary>
public sealed class VerificationMailSender(SmtpOptions options, ILogger<VerificationMailSender> logger) : IVerificationMailSender
{
    public async Task SendCodeAsync(string email, string code, CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            logger.LogWarning("SMTP is not configured; verification code for {Email} is {Code}", email, code);
            return;
        }

        using var message = new MailMessage(options.From, email, "FastTranslation 验证码", $"您的验证码是 {code}，{10} 分钟内有效。");
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(options.User))
        {
            client.Credentials = new NetworkCredential(options.User, options.Password);
        }

        await client.SendMailAsync(message, ct);
    }
}
