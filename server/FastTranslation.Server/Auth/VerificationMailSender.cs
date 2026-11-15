using System.Net;
using System.Net.Mail;

namespace FastTranslation.Server.Auth;

public interface IVerificationMailSender
{
    Task SendCodeAsync(string email, string code, CancellationToken ct);
}

/// <summary>
/// 已配置时走 SMTP 投递；开发环境未配置 SMTP 时把验证码写进日志，
/// 让开发者在本机完成注册。生产环境绝不把验证码写进日志——日志会被
/// 运维读取、备份带走，等于把账号接管凭证落到磁盘上。
/// </summary>
public sealed class VerificationMailSender(
    SmtpOptions options, ILogger<VerificationMailSender> logger, IHostEnvironment environment) : IVerificationMailSender
{
    public async Task SendCodeAsync(string email, string code, CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("SMTP is not configured; verification code for {Email} is {Code}", email, code);
            }
            else
            {
                logger.LogError("SMTP is not configured; verification code for {Email} was not sent", email);
            }
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
