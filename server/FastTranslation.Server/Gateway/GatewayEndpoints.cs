using System.Security.Claims;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// The proxy gateway. It is deliberately shaped as an OpenAI-compatible endpoint
/// (<c>POST /v1/chat/completions</c>) so the existing desktop provider needs no protocol work:
/// it points <c>QwenMtOptions.BaseUrl</c> at <c>{server}/v1</c> and sends its access token where it
/// used to send the API key (design §5 phase 2).
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>Where the desktop client's device fingerprint rides. Optional; anonymous rows reuse one device.</summary>
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
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "body_invalid");
        }

        var (model, chars) = ReadChatShape(body);
        if (chars <= 0)
        {
            return Fail(StatusCodes.Status400BadRequest, "text_required");
        }

        if (chars > gateway.MaxRequestChars)
        {
            return Fail(StatusCodes.Status413PayloadTooLarge, "text_too_long");
        }

        if (Intent(context, principal, store, database, gateway, limiter, chars) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendChatAsync(body, ct);
        if (!response.Succeeded)
        {
            // Upstream refused: forward the status so the client's existing classifier still works,
            // and do not charge the account for a translation it never received.
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

        store.RecordUsage(
            userId,
            deviceId,
            chars,
            model,
            (int)Math.Min(tokensIn, int.MaxValue),
            (int)Math.Min(tokensOut, int.MaxValue),
            Pricing.TextCostMicros(model, tokensIn, tokensOut),
            database.NowUnix);

        return Results.Content(response.Body, "application/json");
    }

    /// <summary>
    /// Screenshot translation through the same key. The upstream API reports no usage for images, so
    /// one request is one billed image and the quota side charges a fixed equivalent (see
    /// <see cref="GatewayOptions.ImageQuotaChars"/>).
    /// </summary>
    private static async Task<IResult> Image(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayRateLimiter limiter,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "body_invalid");
        }

        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        if (Intent(context, principal, store, database, gateway, limiter, quotaChars) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendImageAsync(body, ct);
        if (!response.Succeeded)
        {
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
    /// Screenshot OCR. The body is forwarded untouched to the multimodal endpoint; the difference from
    /// <see cref="ChatCompletions"/> is that the payload carries an image rather than text, so there is
    /// no character ceiling to police and the quota is charged as one screenshot (same equivalent as
    /// <see cref="Image"/>). Cost, however, is real token accounting: the model bills vision tokens even
    /// though the quota side thinks in characters.
    /// </summary>
    private static async Task<IResult> Ocr(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayRateLimiter limiter,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        var body = await ReadBodyAsync(context, ct);
        if (body is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "body_invalid");
        }

        // One OCR is priced like one image translation for quota purposes: both take a picture in.
        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        if (Intent(context, principal, store, database, gateway, limiter, quotaChars) is { } refusal)
        {
            return refusal;
        }

        var response = await upstream.SendOcrAsync(body, ct);
        if (!response.Succeeded)
        {
            // Same rule as chat: forward the upstream status verbatim and never bill a refusal.
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
    /// Every gate a request must clear, in the order that never spends money it cannot bill:
    /// rate limit → deployment budget → account quota. Returns null when the request may proceed.
    /// </summary>
    private static IResult? Intent(
        HttpContext context,
        ClaimsPrincipal principal,
        AccountStore store,
        ServerDatabase database,
        GatewayOptions gateway,
        GatewayRateLimiter limiter,
        int chars)
    {
        var userId = principal.GetUserId();

        if (!limiter.TryAcquire(userId, gateway.RequestsPerMinute))
        {
            return Fail(StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        if (gateway.DailyBudgetMicros > 0 && store.ReadTodayCostMicros() >= gateway.DailyBudgetMicros)
        {
            return Fail(StatusCodes.Status429TooManyRequests, "budget_exhausted");
        }

        var user = store.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Fail(StatusCodes.Status403Forbidden, "account_disabled");
        }

        var (deviceId, _) = Device(context, principal, store);
        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        var usage = store.ReadUsage(userId, deviceId);
        var decision = QuotaPolicy.Evaluate(tier, usage.MonthlyChars, usage.DailyChars, chars);
        if (!decision.Allowed)
        {
            // 429 (not 403) on purpose: the client reads it as "out of quota" and silently switches to
            // the user's own key, so an exhausted plan degrades the product instead of breaking it.
            context.Response.Headers["X-FT-Quota-Reason"] = decision.Reason ?? "quota_exhausted";
            return Fail(StatusCodes.Status429TooManyRequests, decision.Reason ?? "quota_exhausted");
        }

        return null;
    }

    private static (Guid DeviceId, Guid UserId) Device(
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

        // RegisterDevice honours the per-account device cap; a refusal here must not break
        // translation, so an over-cap client simply records against the anonymous device row.
        var registered = store.RegisterDevice(userId, fingerprint, string.Empty);
        return registered.Device is { } device
            ? (device.Id, userId)
            : (store.FindDeviceByFingerprint(userId, AnonymousFingerprint)?.Id ?? Guid.Empty, userId);
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
    /// Qwen-MT bills a fixed ~45-token preamble on every request regardless of length, so a bare
    /// character count would under-report cost badly. This mirrors that overhead and is only used
    /// when the upstream response carries no usage block.
    /// </summary>
    private static long EstimateInputTokens(int chars) => chars + 45L;

    private static async Task<string?> ReadBodyAsync(HttpContext context, CancellationToken ct)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static IResult Fail(int status, string error)
        => Results.Json(new ErrorResponse(error), statusCode: status);
}
