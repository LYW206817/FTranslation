using System.Globalization;
using System.Threading.RateLimiting;

namespace FastTranslation.Server.Api;

/// <summary>
/// Fixed-window per-IP limiting on the auth endpoints. Deliberately IP-keyed only for auth:
/// quota enforcement must NOT key on IP, because campus/corporate NAT would punish real users
/// (design §7.4) — quota is per account and per device instead.
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
