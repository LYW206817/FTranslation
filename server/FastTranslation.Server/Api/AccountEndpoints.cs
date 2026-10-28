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

    private static IResult Me(ClaimsPrincipal principal, AccountStore store)
    {
        var user = store.FindUserById(principal.GetUserId());
        return user is null
            ? Results.NotFound(new ErrorResponse("account_missing"))
            : Results.Ok(AuthEndpoints.ToAccountDto(user));
    }

    /// <summary>
    /// Usage is the client-visible consumption view (requirement: the user must be able to see what
    /// they have spent). The device id is optional — without it only the account-wide month is shown.
    /// </summary>
    private static IResult Usage(ClaimsPrincipal principal, [FromQuery] Guid? deviceId, AccountStore store)
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

        return Results.Ok(AuthEndpoints.ToUsageDto(store.ReadUsage(user.Id, deviceId), user.Plan));
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
    }

    private static IResult Metrics(AccountStore store)
    {
        var (total, free, light, standard, active) = store.ReadAccountMetrics();
        return Results.Ok(new AdminMetricsResponse(total, free, light, standard, active));
    }

    private static IResult SetPlan(
        string email,
        [FromBody] PlanChangeRequest request,
        AccountStore store)
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

        store.SetPlan(user.Id, plan);
        return Results.Ok(new MessageResponse("plan_updated"));
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
