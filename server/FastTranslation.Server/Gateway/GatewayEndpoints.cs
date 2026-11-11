using System.Security.Claims;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 代理网关。刻意做成 OpenAI 兼容端点的形态
/// （<c>POST /v1/chat/completions</c>），这样现有桌面端 provider 无需改协议：
/// 它把 <c>QwenMtOptions.BaseUrl</c> 指向 <c>{server}/v1</c>，并把 access token 发到原先发
/// API key 的位置（设计 §5 阶段二）。
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>桌面客户端的设备指纹从这里传。可选；匿名记录复用同一个设备。</summary>
    public const string DeviceHeaderName = "X-FT-Device";

    private const string AnonymousFingerprint = "gateway-anonymous";

    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1").WithTags("gateway").RequireAuthorization();

        group.MapPost("/chat/completions", ChatCompletions);
        group.MapPost("/image", Image);
        group.MapPost("/ocr", Ocr);
    }

    private static async Task<IResult> ChatCompletions(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayRateLimiter limiter,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        diagnostics.RecordGatewayRequest();
        if (!gateway.IsConfigured)
        {
            return Fail(diagnostics, StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        var (model, chars) = ReadChatShape(body);
        if (chars <= 0)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "text_required");
        }

        if (chars > gateway.MaxRequestChars)
        {
            return Fail(diagnostics, StatusCodes.Status413PayloadTooLarge, "text_too_long");
        }

        if (Intent(context, principal, store, database, gateway, limiter, diagnostics, Pricing.QuotaChars(model, chars)) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendChatAsync(body, ct);
        if (!response.Succeeded)
        {
            // 上游拒绝：透传状态码，让客户端现有的分类逻辑继续工作，
            // 并且不为这次根本没拿到的翻译向账号收费。
            diagnostics.RecordGatewayFailure($"upstream_{response.StatusCode}");
            return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
        }

        var (deviceId, userId) = Device(context, principal, store);
        var tokensIn = EstimateInputTokens(chars);
        var tokensOut = 0L;
        if (DashScopeGateway.TryReadUsage(response.Body, out var reportedIn, out var reportedOut))
        {
            tokensIn = reportedIn;
            tokensOut = reportedOut;
        }

        // v97: 额度记「flash 等价字符」而不是原始字符——plus 的真实成本是 flash 的 2.68 倍，
        // 按 1:1 记会让用户用低价档买走高价成本。预检与记账必须用同一个折算值，
        // 否则闸门按 100 放行、账上记 270，剩余额度能被 plus 直接透支掉。
        store.RecordUsage(
            userId,
            deviceId,
            Pricing.QuotaChars(model, chars),
            model,
            (int)Math.Min(tokensIn, int.MaxValue),
            (int)Math.Min(tokensOut, int.MaxValue),
            Pricing.TextCostMicros(model, tokensIn, tokensOut),
            database.NowUnix);

        return Results.Content(response.Body, "application/json");
    }

    /// <summary>
    /// 用同一把 Key 做截图翻译。上游 API 对图像不报告用量，所以
    /// 一次请求即一张计费图片，额度侧按固定折算值扣费（见
    /// <see cref="GatewayOptions.ImageQuotaChars"/>）。截图流程会同时调用本端点与
    /// <c>/v1/ocr</c>，两个 1,000 合起来就是产品口径的「每张截图 2,000 字符起」。
    /// </summary>
    private static async Task<IResult> Image(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayRateLimiter limiter,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        diagnostics.RecordGatewayRequest();
        if (!gateway.IsConfigured)
        {
            return Fail(diagnostics, StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        if (Intent(context, principal, store, database, gateway, limiter, diagnostics, quotaChars) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendImageAsync(body, ct);
        if (!response.Succeeded)
        {
            diagnostics.RecordGatewayFailure($"upstream_{response.StatusCode}");
            return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
        }

        var (deviceId, userId) = Device(context, principal, store);
        var model = ReadImageModel(body) ?? gateway.Model;
        store.RecordUsage(
            userId,
            deviceId,
            quotaChars,
            model,
            0,
            0,
            Pricing.ImageCostMicros(model),
            database.NowUnix);

        return Results.Content(response.Body, "application/json");
    }

    /// <summary>
    /// 截图 OCR。请求体原样转发到多模态端点；与
    /// <see cref="ChatCompletions"/> 的区别在于载荷是一张图而不是文本，因此没有
    /// 需要管制的字符上限，额度按一次截图扣费（与
    /// <see cref="Image"/> 相同折算）。但成本是真实的 token 记账：模型按视觉 token 计费，
    /// 即便额度侧是以字符为单位思考的。
    /// </summary>
    private static async Task<IResult> Ocr(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayRateLimiter limiter,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        diagnostics.RecordGatewayRequest();
        if (!gateway.IsConfigured)
        {
            return Fail(diagnostics, StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        // 从额度角度看，一次 OCR 与一次图片翻译定价相同：两者都是输入一张图。
        // 截图流程会同时调用 /v1/image，两个 1,000 合起来就是「每张截图 2,000 字符起」。
        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        if (Intent(context, principal, store, database, gateway, limiter, diagnostics, quotaChars) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendOcrAsync(body, ct);
        if (!response.Succeeded)
        {
            // 与 chat 同规则：原样透传上游状态码，绝不为拒绝计费。
            diagnostics.RecordGatewayFailure($"upstream_{response.StatusCode}");
            return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
        }

        var (deviceId, userId) = Device(context, principal, store);
        var model = string.IsNullOrWhiteSpace(gateway.OcrModel) ? "qwen-vl-ocr" : gateway.OcrModel.Trim();
        var tokensIn = (long)quotaChars;
        var tokensOut = 0L;
        if (DashScopeGateway.TryReadVisionUsage(response.Body, out var reportedIn, out var reportedOut) && reportedIn > 0)
        {
            tokensIn = reportedIn;
            tokensOut = reportedOut;
        }

        store.RecordUsage(
            userId,
            deviceId,
            quotaChars,
            model,
            (int)Math.Min(tokensIn, int.MaxValue),
            (int)Math.Min(tokensOut, int.MaxValue),
            Pricing.TextCostMicros(model, tokensIn, tokensOut),
            database.NowUnix);

        return Results.Content(response.Body, "application/json");
    }

    /// <summary>
    /// 请求必须通过的每一道闸门，顺序保证不会花掉无法计费的钱：
    /// 限流 → 部署预算 → 账号额度。请求可继续时返回 null。
    /// </summary>
    private static IResult? Intent(
        HttpContext context,
        ClaimsPrincipal principal,
        AccountStore store,
        ServerDatabase database,
        GatewayOptions gateway,
        GatewayRateLimiter limiter,
        GatewayDiagnostics diagnostics,
        int chars)
    {
        var userId = principal.GetUserId();

        if (!limiter.TryAcquire(userId, gateway.RequestsPerMinute))
        {
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        if (gateway.DailyBudgetMicros > 0 && store.ReadTodayCostMicros() >= gateway.DailyBudgetMicros)
        {
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "budget_exhausted");
        }

        var user = store.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Fail(diagnostics, StatusCodes.Status403Forbidden, "account_disabled");
        }

        var (deviceId, _) = Device(context, principal, store);
        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        var usage = store.ReadUsage(userId, deviceId);
        var decision = QuotaPolicy.Evaluate(tier, usage.MonthlyChars, usage.DailyChars, chars);
        if (!decision.Allowed)
        {
            // 故意用 429（而不是 403）：客户端会把它读作“额度用尽”并静默切换到
            // 用户自己的 Key，于是套餐耗尽只是让产品降级，而不是把它弄坏。
            context.Response.Headers["X-FT-Quota-Reason"] = decision.Reason ?? "quota_exhausted";
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, decision.Reason ?? "quota_exhausted");
        }

        return null;
    }

    internal static (Guid DeviceId, Guid UserId) Device(
        HttpContext context,
        ClaimsPrincipal principal,
        AccountStore store)
    {
        var userId = principal.GetUserId();
        var fingerprint = context.Request.Headers[DeviceHeaderName].ToString().Trim();
        if (fingerprint.Length == 0)
        {
            fingerprint = AnonymousFingerprint;
        }

        if (store.FindDeviceByFingerprint(userId, fingerprint) is { } known)
        {
            return (known.Id, userId);
        }

        // RegisterDevice 会遵守每账号设备上限；这里被拒绝也不能中断
        // 翻译，所以超限的客户端直接记到匿名设备行上。
        var registered = store.RegisterDevice(userId, fingerprint, string.Empty);
        if (registered.Device is { } device)
        {
            return (device.Id, userId);
        }

        // v127: 匿名行不一定存在（桌面端每次请求都带指纹）。这时必须退回到账号已有
        // 的一台设备，绝不能返回 Guid.Empty：usage_daily 对 devices 有外键，记账会
        // 抛异常返回 500，而上游翻译已经调用、成本已经发生。
        var fallback = store.FindDeviceByFingerprint(userId, AnonymousFingerprint)
            ?? store.ListDevices(userId).FirstOrDefault();
        return (fallback?.Id ?? Guid.Empty, userId);
    }

    private static (string Model, int Chars) ReadChatShape(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;

            var chars = 0;
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        chars += content.GetString()?.Length ?? 0;
                    }
                }
            }

            return (model, chars);
        }
        catch (JsonException)
        {
            return (string.Empty, 0);
        }
    }

    private static string? ReadImageModel(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Qwen-MT 对每次请求固定收取约 45 token 的前导开销，与长度无关，所以只按
    /// 字符数会严重低报成本。这里补上这块开销，仅在
    /// 上游响应没有 usage 块时使用。
    /// </summary>
    private static long EstimateInputTokens(int chars) => chars + 45L;

    private static async Task<string?> ReadBodyAsync(HttpContext context, CancellationToken ct)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static IResult Fail(GatewayDiagnostics diagnostics, int status, string error)
    {
        diagnostics.RecordGatewayFailure(error);
        return Results.Json(new ErrorResponse(error), statusCode: status);
    }
}
