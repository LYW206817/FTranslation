using System.Globalization;
using System.Security.Claims;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 语音中继端点。四道闸门在 WebSocket 升级之前跑完,失败按普通 HTTP 返回——
/// 客户端在连接阶段就能拿到 429/401,而不是连上之后被掐(设计 §6.1)。
/// </summary>
public static class SpeechEndpoints
{
    public static void MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/v1/asr", SpeechAsync)
            .RequireAuthorization()
            .WithTags("gateway");
    }

    private static async Task SpeechAsync(HttpContext context, ClaimsPrincipal principal,
        AccountStore store, ServerDatabase database, GatewayOptions gateway, GatewayRateLimiter limiter)
    {
        if (!gateway.Speech.Enabled || string.IsNullOrWhiteSpace(gateway.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("speech_disabled"));
            return;
        }

        var userId = principal.GetUserId();
        if (!limiter.TryAcquire(userId, gateway.RequestsPerMinute))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("rate_limited"));
            return;
        }

        // 语音的独立日预算:按 speech 来源单独记一个桶,免得语音烧完把文本翻译一起 429。
        if (gateway.Speech.DailyBudgetMicros > 0
            && store.ReadTodayCostMicros("speech") >= gateway.Speech.DailyBudgetMicros)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("budget_exhausted"));
            return;
        }

        var user = store.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("account_disabled"));
            return;
        }

        // 额度按秒折字符:按最小会话(MinSeconds×QuotaCharsPerSecond)预检,连 10 秒都讲不起就别进来。
        var requestedChars = gateway.Speech.MinSeconds * gateway.Speech.QuotaCharsPerSecond;
        var (deviceId, _) = GatewayEndpoints.Device(context, principal, store);
        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        var usage = store.ReadUsage(userId, deviceId);
        var decision = QuotaPolicy.Evaluate(tier, usage.MonthlyChars, usage.DailyChars, requestedChars);
        if (!decision.Allowed)
        {
            // 故意 429 而不是 403:客户端把它读成额度用尽,静默引导升级或自备 Key。
            context.Response.Headers["X-FT-Quota-Reason"] = decision.Reason ?? "quota_exhausted";
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(decision.Reason ?? "quota_exhausted"));
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            // 闸门全过但没带升级头:HTTP 测试就是从这条 400 收口的。
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var speechGateway = new DashScopeSpeechGateway(gateway.Speech, gateway.ApiKey);
        await speechGateway.RelayAsync(socket, userId, deviceId, store, database, context.RequestAborted);
    }
}