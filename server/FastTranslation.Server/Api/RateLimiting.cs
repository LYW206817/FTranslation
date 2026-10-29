using System.Globalization;
using System.Threading.RateLimiting;

namespace FastTranslation.Server.Api;

/// <summary>
/// 认证类接口上按 IP 的固定窗口限流。刻意只在认证场景按 IP 限流：
/// 额度执行绝不能按 IP 做键，否则校园/企业 NAT 会误伤真实用户
/// （设计 §7.4）——额度改为按账号和按设备计。
/// </summary>
public static class RateLimiting
{
    public const string AuthPolicy = "auth";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicy, context =>
            {
                var configured = context.RequestServices.GetRequiredService<ServerOptions>().AuthRequestsPerMinute;
                var permitLimit = configured > 0 ? configured : 20;
                var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });
    }

    public static string RetryAfterHeader(int seconds)
        => seconds.ToString(CultureInfo.InvariantCulture);
}
