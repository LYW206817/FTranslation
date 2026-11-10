using System.Globalization;
using System.Threading.RateLimiting;

namespace FastTranslation.Server.Api;

/// <summary>
/// 认证与管理接口上按 IP 的固定窗口限流。额度执行绝不能按 IP 做键，
/// 否则校园/企业 NAT 会误伤真实用户（设计 §7.4）——额度改为按账号和按设备计。
/// </summary>
public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string AdminPolicy = "admin";

    public static IServiceCollection AddRateLimitingPolicies(this IServiceCollection services)
    {
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicy, context =>
            {
                var configured = context.RequestServices.GetRequiredService<ServerOptions>().AuthRequestsPerMinute;
                return IpPartition(context, configured > 0 ? configured : 20);
            });

            options.AddPolicy(AdminPolicy, context =>
            {
                var configured = context.RequestServices.GetRequiredService<ServerOptions>().AdminRequestsPerMinute;
                return IpPartition(context, configured > 0 ? configured : 20);
            });
        });
    }

    private static RateLimitPartition<string> IpPartition(HttpContext context, int permitLimit)
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public static string RetryAfterHeader(int seconds)
        => seconds.ToString(CultureInfo.InvariantCulture);
}
