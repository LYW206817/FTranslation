using System.Globalization;
using System.Security.Claims;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 语音中继端点。四道闸门在 WebSocket 升级之前跑完,失败按普通 HTTP 返回——
/// 客户端在连接阶段就能拿到 429/401,而不是连上之后被掐(设计 §6.1)。
/// </summary>
public static class SpeechEndpoints
{
    private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromSeconds(5);

    public static void MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/v1/asr", SpeechAsync)
            .RequireAuthorization()
            .WithTags("gateway");
    }

    private static async Task SpeechAsync(HttpContext context, ClaimsPrincipal principal,
        AccountStore store, ServerDatabase database, GatewayOptions gateway, GatewayRateLimiter limiter,
        SpeechSessionLimiter sessions, GatewayBudgetGuard budget, GatewayDiagnostics diagnostics,
        IHostApplicationLifetime appLifetime)
    {
        diagnostics.RecordSpeechRequest();

        if (!gateway.Speech.Enabled || string.IsNullOrWhiteSpace(gateway.ApiKey))
        {
            diagnostics.RecordSpeechFailure("speech_disabled");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("speech_disabled"));
            return;
        }

        var userId = principal.GetUserId();
        if (!limiter.TryAcquire(userId, gateway.RequestsPerMinute))
        {
            diagnostics.RecordSpeechFailure("rate_limited");
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("rate_limited"));
            return;
        }

        // 语音的独立日预算:按 speech 来源单独记一个桶,免得语音烧完把文本翻译一起 429。
        // 预留放在算出本场会话的硬顶之后（见下），否则不知道要占多少成本。
        var user = store.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            diagnostics.RecordSpeechFailure("account_disabled");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("account_disabled"));
            return;
        }

        // 额度按秒折字符:按最小会话(MinSeconds×QuotaCharsPerSecond)预检,连最短会话都讲不起就别进来。
        var requestedChars = gateway.Speech.MinSeconds * gateway.Speech.QuotaCharsPerSecond;
        var deviceId = principal.GetDeviceId();
        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        var usage = store.ReadUsage(userId, deviceId);
        var decision = QuotaPolicy.Evaluate(tier, usage.MonthlyChars, usage.DailyChars, requestedChars);
        if (!decision.Allowed)
        {
            // 故意 429 而不是 403:客户端把它读成额度用尽,静默引导升级或自备 Key。
            var reason = decision.Reason ?? "quota_exhausted";
            diagnostics.RecordSpeechFailure(reason);
            context.Response.Headers["X-FT-Quota-Reason"] = reason;
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(reason));
            return;
        }

        // 会话硬顶取「剩余额度还能讲多久」与 MaxSecondsPerSession 的较小值:预检只验了
        // 最小会话的 200 字符,不按剩余额度截断的话,一次讲满 30 分钟就是 36,000 字符,
        // 能把日/月额度顶穿十几倍(v81)。
        var remaining = Math.Min(decision.RemainingMonthly, decision.RemainingDaily ?? int.MaxValue);
        var sessionSeconds = SpeechQuota.SessionSeconds(remaining, gateway.Speech.QuotaCharsPerSecond,
            gateway.Speech.MinSeconds, gateway.Speech.MaxSecondsPerSession);

        // 整场会话的最大额度一次占用：只预检最小会话的话，并发的文本请求可以在这条
        // 会话讲完之前把剩余额度花掉，语音结束时记账就会超发。实耗由中继按秒记账，
        // 结束时把剩余预留退回。
        var reservedChars = checked(sessionSeconds * gateway.Speech.QuotaCharsPerSecond);
        var reservation = store.TryReserveQuota(userId, deviceId, reservedChars, tier, out var quotaId);
        if (!reservation.Allowed)
        {
            var reason = reservation.Reason ?? "quota_exhausted";
            diagnostics.RecordSpeechFailure(reason);
            context.Response.Headers["X-FT-Quota-Reason"] = reason;
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(reason));
            return;
        }

        var estimatedCostMicros = Pricing.EstimateSpeechCostMicros(gateway.Speech.Model, sessionSeconds);
        var budgetHeld = false;
        var sessionEntered = false;
        try
        {
            budgetHeld = budget.TryReserve(GatewayBuckets.Speech, gateway.Speech.DailyBudgetMicros, estimatedCostMicros);
            if (!budgetHeld)
            {
                diagnostics.RecordSpeechFailure("budget_exhausted");
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new ErrorResponse("budget_exhausted"));
                return;
            }

            // 限频不能代替并发限制，长连接占用资源直到会话结束。
            sessionEntered = sessions.TryEnter(userId, gateway.Speech.MaxConcurrentSessionsPerUser);
            if (!sessionEntered)
            {
                diagnostics.RecordSpeechFailure("speech_busy");
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new ErrorResponse("speech_busy"));
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                // 闸门全过但没带升级头:HTTP 测试与客户端的升级前探测都从这条 400 收口。
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var speechGateway = new DashScopeSpeechGateway(gateway.Speech, gateway.ApiKey);
            // v210: Kestrel 停机不会主动取消已升级的连接（要等满 ShutdownTimeout，默认 30 秒），
            // 而 systemd 20 秒就 SIGKILL——不联动 ApplicationStopping 的话，正在语音时重启
            // 会跳过收尾：上游收不到 finish-task，尾段用量也不结算。
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, appLifetime.ApplicationStopping);
            var authorizer = WatchSessionAsync(store, diagnostics, userId, principal.GetSessionId(), connectionLifetime);
            try
            {
                await speechGateway.RelayAsync(socket, userId, deviceId, store, database, sessionSeconds,
                    quotaId, connectionLifetime.Token);
            }
            finally
            {
                connectionLifetime.Cancel();
                await authorizer;
            }
        }
        catch (Exception)
        {
            // 只为诊断计数，异常照原样抛出；吞掉会让会话错误从日志里消失。
            diagnostics.RecordSpeechFailure("relay_error");
            throw;
        }
        finally
        {
            try { store.ReleaseQuota(quotaId); }
            finally
            {
                if (sessionEntered) sessions.Exit(userId);
                if (budgetHeld) budget.Release(GatewayBuckets.Speech, estimatedCostMicros);
            }
        }
    }

    private static async Task WatchSessionAsync(AccountStore store, GatewayDiagnostics diagnostics,
        Guid userId, Guid sessionId, CancellationTokenSource lifetime)
    {
        // 已升级的连接不会再经过 Bearer 中间件，需要在会话结束前主动跟随撤销。
        using var timer = new PeriodicTimer(SessionCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (store.FindSessionDevice(userId, sessionId) is null)
                {
                    lifetime.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 客户端退出或会话结束，主动停止轮询。
        }
        catch (SqliteException)
        {
            diagnostics.RecordSpeechFailure("session_check_failed");
            lifetime.Cancel();
        }
    }
}
