using System.Security.Claims;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account").WithTags("account").RequireAuthorization();

        group.MapGet("/me", Me);
        group.MapGet("/usage", Usage);
        group.MapPost("/devices", RegisterDevice);
    }

    private static IResult Me(ClaimsPrincipal principal, AccountStore store, ServerDatabase database)
    {
        var user = store.FindUserById(principal.GetUserId());
        return user is null
            ? Results.NotFound(new ErrorResponse("account_missing"))
            : Results.Ok(AuthEndpoints.ToAccountDto(user, database));
    }

    /// <summary>
    /// Usage is the client-visible consumption view (requirement: the user must be able to see what
    /// they have spent). The device id is optional — without it only the account-wide month is shown.
    /// </summary>
    private static IResult Usage(
        ClaimsPrincipal principal,
        [FromQuery] Guid? deviceId,
        [FromQuery] string? fingerprint,
        AccountStore store,
        ServerDatabase database)
    {
        var user = store.FindUserById(principal.GetUserId());
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        if (deviceId is { } id && store.FindDevice(user.Id, id) is null)
        {
            return Results.NotFound(new ErrorResponse("device_missing"));
        }

        // The gateway bills the daily quota per device, so a client that only knows its fingerprint
        // (every client does, it is what sign-in registers) must be able to ask for its own day.
        if (deviceId is null && !string.IsNullOrWhiteSpace(fingerprint)
            && store.FindDeviceByFingerprint(user.Id, fingerprint.Trim()) is { } resolved)
        {
            deviceId = resolved.Id;
        }

        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        return Results.Ok(AuthEndpoints.ToUsageDto(store.ReadUsage(user.Id, deviceId), tier));
    }

    private static IResult RegisterDevice(
        [FromBody] DeviceRequest request,
        ClaimsPrincipal principal,
        AccountStore store)
    {
        if (string.IsNullOrWhiteSpace(request.Fingerprint))
        {
            return Results.BadRequest(new ErrorResponse("fingerprint_required"));
        }

        var device = store.RegisterDevice(principal.GetUserId(), request.Fingerprint, request.Name ?? string.Empty);
        return device.Device is { } registered
            ? Results.Ok(new DeviceDto(registered.Id, registered.Name, registered.CreatedAt, registered.LastSeenAt))
            : Results.BadRequest(new ErrorResponse(device.Error!));
    }
}

/// <summary>
/// Admin surface. Guarded by a shared secret in the "X-Admin-Token" header — this exists so the
/// operator can watch registration and consumption without a separate dashboard in phase 0.
/// </summary>
public static class AdminEndpoints
{
    public const string HeaderName = "X-Admin-Token";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("admin");
        group.MapGet("/metrics", Metrics).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/accounts/{email}/plan", SetPlan).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/codes", CreateCodes).AddEndpointFilter(RequireAdminToken);
    }

    private static IResult Metrics(AccountStore store)
    {
        var (total, free, light, standard, active) = store.ReadAccountMetrics();
        return Results.Ok(new AdminMetricsResponse(total, free, light, standard, active));
    }

    private static IResult SetPlan(
        string email,
        [FromBody] PlanChangeRequest request,
        AccountStore store,
        ServerDatabase database)
    {
        if (!Plans.TryParse(request.Plan, out var plan))
        {
            return Results.BadRequest(new ErrorResponse("plan_invalid"));
        }

        var user = store.FindUserByEmail(email);
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        // Days is a relative length so the operator never has to compute a unix timestamp by hand.
        long? expiresAt = request.Days switch
        {
            null => user.PlanExpiresAt,
            0 => null,
            { } days and > 0 => database.NowUnix + (days * 86_400L),
            _ => null,
        };

        store.SetPlan(user.Id, plan, expiresAt);
        return Results.Ok(new MessageResponse("plan_updated"));
    }

/// <summary>
    /// Mints one batch of activation codes. Kept next to SetPlan because both are the operator
    /// handing out paid time; unlike SetPlan these codes are bearer instruments, so they are written
    /// down and the operator has to keep the response.
    /// </summary>
    private static IResult CreateCodes(
        [FromBody] CreateCodesRequest request,
        RedeemStore codes,
        ServerDatabase database)
    {
        if (!Plans.TryParse(request.Plan, out var plan) || plan == PlanTier.Free)
        {
            return Results.BadRequest(new ErrorResponse("plan_invalid"));
        }

        var days = request.Days ?? 30;
        if (days is <= 0 or > 3_650)
        {
            return Results.BadRequest(new ErrorResponse("days_invalid"));
        }

        var count = request.Count ?? 1;
        if (count is <= 0 or > 200)
        {
            return Results.BadRequest(new ErrorResponse("count_invalid"));
        }

        var generated = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            generated.Add(RedeemCode.Generate());
        }

        codes.CreateCodes(generated, plan, days, request.Note ?? string.Empty, database.NowUnix);
        return Results.Ok(new CreateCodesResponse(generated, Plans.ToWire(plan), days));
    }

    private static async ValueTask<object?> RequireAdminToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = context.HttpContext.RequestServices.GetRequiredService<ServerOptions>().AdminToken;
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(configured) || !CredentialEquals(configured, provided))
        {
            return Results.Json(new ErrorResponse("admin_forbidden"), statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    private static bool CredentialEquals(string expected, string provided)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
