using System.Security.Claims;
using FastTranslation.Server.Auth;
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
        group.MapPatch("/profile", UpdateProfile);
        group.MapPost("/password", ChangePassword);
    }

    private static IResult Me(ClaimsPrincipal principal, AccountStore store, ServerDatabase database)
    {
        var user = store.FindUserById(principal.GetUserId());
        return user is null
            ? Results.NotFound(new ErrorResponse("account_missing"))
            : Results.Ok(AuthEndpoints.ToAccountDto(user, database));
    }

    /// <summary>
    /// 用量是客户端可见的消耗视图（需求要求：用户必须能看到自己花了多少）。
    /// 设备 id 可选——不传时只显示账号维度的月度用量。
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

        // 网关按设备扣每日额度，所以只知道自身指纹的客户端（每个客户端都是如此，
        // 登录时注册的就是它）必须能查到自己的当日用量。
        if (deviceId is null && !string.IsNullOrWhiteSpace(fingerprint)
            && store.FindDeviceByFingerprint(user.Id, fingerprint.Trim()) is { } resolved)
        {
            deviceId = resolved.Id;
        }

        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        return Results.Ok(AuthEndpoints.ToUsageDto(store.ReadUsage(user.Id, deviceId), tier));
    }

    /// <summary>
    /// 唯一可编辑的资料字段。这里检查长度与控制字符，因为这个值会渲染在
    /// 窗口标题区和侧边栏，一个换行或 200 字符长的名字不只是难看，
    /// 而是会直接撑坏布局。
    /// </summary>
    private static IResult UpdateProfile(
        [FromBody] UpdateProfileRequest request,
        ClaimsPrincipal principal,
        AccountStore store,
        ServerDatabase database)
    {
        var user = store.FindUserById(principal.GetUserId());
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        var name = (request.DisplayName ?? string.Empty).Trim();
        if (name.Length > 24)
        {
            return Results.BadRequest(new ErrorResponse("display_name_too_long"));
        }

        if (name.Any(char.IsControl))
        {
            return Results.BadRequest(new ErrorResponse("display_name_invalid"));
        }

        store.SetDisplayName(user.Id, name);
        var updated = store.FindUserById(user.Id)!;
        return Results.Ok(AuthEndpoints.ToAccountDto(updated, database));
    }

    /// <summary>
    /// 改密码会吊销所有 refresh token，并立即给本设备签发一对新 token：
    /// 其他设备必须重新登录，而做出这次修改的设备不会被
    /// 中途踢下线。已发出的 access token 在过期前（30 分钟）仍然有效。
    /// </summary>
    private static IResult ChangePassword(
        [FromBody] ChangePasswordRequest request,
        ClaimsPrincipal principal,
        AccountStore store,
        TokenService tokens,
        ServerOptions options,
        ServerDatabase database)
    {
        var user = store.FindUserById(principal.GetUserId());
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            return Results.Json(new ErrorResponse("password_mismatch"), statusCode: StatusCodes.Status400BadRequest);
        }

        var next = request.NewPassword ?? string.Empty;
        if (next.Length < 8)
        {
            return Results.BadRequest(new ErrorResponse("password_too_short"));
        }

        if (PasswordHasher.Verify(next, user.PasswordHash))
        {
            return Results.BadRequest(new ErrorResponse("password_unchanged"));
        }

        store.SetPasswordHash(user.Id, PasswordHasher.Hash(next));
        store.RevokeAllRefreshTokens(user.Id);

        var device = store.RegisterDevice(
            user.Id,
            string.IsNullOrWhiteSpace(request.DeviceFingerprint) ? "password-change" : request.DeviceFingerprint.Trim(),
            request.DeviceName ?? string.Empty);
        if (device.Device is not { } registered)
        {
            return Results.BadRequest(new ErrorResponse(device.Error!));
        }

        return Results.Ok(AuthEndpoints.IssueTokens(store, tokens, options, database, user, registered));
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
/// 管理面。用 "X-Admin-Token" 头里的共享密钥保护——它存在的意义是让运维
/// 在阶段零不必另做后台看板就能观察注册与消耗。
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

        // Days 是相对天数，运维不必手算 Unix 时间戳。
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
    /// 生成一批激活码。放在 SetPlan 旁边是因为两者都是运维在发放付费时长；
    /// 与 SetPlan 不同，这些码是凭据性质的，所以会被写下来，
    /// 运维必须保留这次响应。
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

    internal static async ValueTask<object?> RequireAdminToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
