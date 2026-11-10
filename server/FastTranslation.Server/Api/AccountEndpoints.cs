using System.Globalization;
using System.Security.Claims;
using FastTranslation.Server.Auth;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Gateway;
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
        group.MapGet("/devices", ListDevices);
        group.MapPost("/devices", RegisterDevice);
        group.MapDelete("/devices/{id:guid}", RemoveDevice);
        group.MapPatch("/profile", UpdateProfile);
        group.MapPost("/password", ChangePassword);
        // 注销是不可逆的破坏性操作，单独挂认证限流，避免拿着有效令牌反复试密码。
        group.MapPost("/delete", DeleteAccount).RequireRateLimiting(RateLimiting.AuthPolicy);
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

    /// <summary>
    /// 注销账号。要求当前密码 + 键入完整邮箱，两道确认都是为了防止「锁屏没关」
    /// 或误点把学习记录删掉。删除是硬删除，不可恢复。
    /// </summary>
    private static IResult DeleteAccount(
        [FromBody] DeleteAccountRequest request,
        ClaimsPrincipal principal,
        AccountStore store)
    {
        var user = store.FindUserById(principal.GetUserId());
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        if (!PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            return Results.Json(new ErrorResponse("password_mismatch"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals((request.Confirmation ?? string.Empty).Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ErrorResponse("confirmation_mismatch"));
        }

        return store.DeleteUser(user.Id, user.Email)
            ? Results.Ok(new MessageResponse("account_deleted"))
            : Results.NotFound(new ErrorResponse("account_missing"));
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
            ? Results.Ok(ToDeviceDto(registered, isCurrent: true))
            : Results.BadRequest(new ErrorResponse(device.Error!));
    }

    /// <summary>
    /// 账号页的设备列表。当前设备按请求头 X-FT-Device 里的指纹标记；客户端
    /// 不送指纹时全部按“非当前”返回，删除接口也不会阻止删任何一台。
    /// </summary>
    private static IResult ListDevices(HttpContext context, ClaimsPrincipal principal, AccountStore store)
    {
        var fingerprint = CallerFingerprint(context);
        var devices = store.ListDevices(principal.GetUserId())
            .Select(device => ToDeviceDto(
                device,
                fingerprint.Length > 0 && string.Equals(device.Fingerprint, fingerprint, StringComparison.Ordinal)))
            .ToList();

        return Results.Ok(new DeviceListResponse(devices, Plans.MaxDevicesPerAccount));
    }

    /// <summary>
    /// 移除一台设备。当前设备不能从自己身上移除：界面已经隐藏了那个按钮，
    /// 这里再挡一次，免得直接调接口把正在用的设备删掉后一脸茫然。
    /// </summary>
    private static IResult RemoveDevice(
        Guid id,
        HttpContext context,
        ClaimsPrincipal principal,
        AccountStore store)
    {
        var userId = principal.GetUserId();
        if (store.FindDevice(userId, id) is not { } device)
        {
            return Results.NotFound(new ErrorResponse("device_missing"));
        }

        var fingerprint = CallerFingerprint(context);
        if (fingerprint.Length > 0 && string.Equals(device.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Results.BadRequest(new ErrorResponse("device_is_current"));
        }

        return store.DeleteDevice(userId, id)
            ? Results.Ok(ToDeviceDto(device, isCurrent: false))
            : Results.NotFound(new ErrorResponse("device_missing"));
    }

    private static string CallerFingerprint(HttpContext context) =>
        context.Request.Headers[GatewayEndpoints.DeviceHeaderName].ToString().Trim();

    private static DeviceDto ToDeviceDto(DeviceRecord device, bool isCurrent) =>
        new(device.Id, device.Name, device.CreatedAt, device.LastSeenAt, isCurrent);
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
        var group = app.MapGroup("/api/v1/admin").WithTags("admin")
            .RequireRateLimiting(RateLimiting.AdminPolicy);
        group.MapGet("/metrics", Metrics).AddEndpointFilter(RequireAdminToken);
        group.MapGet("/usage", Usage).AddEndpointFilter(RequireAdminToken);
        group.MapGet("/accounts", Accounts).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/accounts/{email}/plan", SetPlan).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/accounts/{email}/disable", Disable).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/accounts/{email}/enable", Enable).AddEndpointFilter(RequireAdminToken);
        group.MapGet("/diagnostics", Diagnostics).AddEndpointFilter(RequireAdminToken);
        group.MapPost("/codes", CreateCodes).AddEndpointFilter(RequireAdminToken);
    }

    private static IResult Metrics(AccountStore store)
    {
        var (total, free, light, standard, active) = store.ReadAccountMetrics();
        return Results.Ok(new AdminMetricsResponse(total, free, light, standard, active));
    }

    private static IResult Usage(
        [FromQuery] int? days,
        AccountStore store,
        ServerDatabase database,
        GatewayOptions gateway)
    {
        var window = days ?? 7;
        if (window is < 1 or > 30)
        {
            return Results.BadRequest(new ErrorResponse("days_invalid"));
        }

        // 窗口按 UTC 整日对齐：days=7 返回今天加前 6 个完整日，正好 7 个日桶，
        // 与 /usage/byDay 的 UTC 口径一致；滚动 168 小时会多出第 8 个半天桶。
        var todayStart = database.NowUnix - (database.NowUnix % 86_400);
        var from = todayStart - ((window - 1) * 86_400L);
        var totals = store.ReadUsageTotals(from);
        var bySource = store.ReadUsageBySource(from)
            .Select(row => new AdminUsageBySource(row.Source, row.Requests, row.Chars, row.CostMicros))
            .ToArray();
        var dayRows = store.ReadUsageByDay(from).ToDictionary(row => row.Day, StringComparer.Ordinal);
        var byDay = new List<AdminUsageByDay>(window);
        for (var i = 0; i < window; i++)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(from + (i * 86_400L))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var requests = 0L;
            var chars = 0L;
            var cost = 0L;
            if (dayRows.TryGetValue(day, out var row))
            {
                requests = row.Requests;
                chars = row.Chars;
                cost = row.CostMicros;
            }
            byDay.Add(new AdminUsageByDay(day, requests, chars, cost));
        }

        return Results.Ok(new AdminUsageResponse(
            window,
            from,
            database.NowUnix,
            new AdminUsageTotals(totals.Requests, totals.Chars, totals.TokensIn, totals.TokensOut, totals.CostMicros),
            bySource,
            byDay,
            new AdminTodayUsage(store.ReadTodayCostMicros(), gateway.DailyBudgetMicros)));
    }

    private static IResult Accounts(
        [FromQuery] string? query,
        [FromQuery] string? plan,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        AccountStore store,
        ServerDatabase database)
    {
        PlanTier? filter = null;
        if (!string.IsNullOrWhiteSpace(plan))
        {
            if (!Plans.TryParse(plan, out var parsed))
            {
                return Results.BadRequest(new ErrorResponse("plan_invalid"));
            }
            filter = parsed;
        }

        var take = limit is > 0 ? Math.Min(limit.Value, 200) : 50;
        var skip = offset is > 0 ? offset.Value : 0;
        var (items, total) = store.ReadAccountPage(query, filter, take, skip);
        var now = database.NowUnix;
        return Results.Ok(new AdminAccountPageResponse(
            items.Select(item => new AdminAccountDto(
                item.Email,
                Plans.ToWire(item.Plan),
                Plans.ToWire(Entitlement.EffectiveTier(item.Plan, item.PlanExpiresAt, now)),
                item.PlanExpiresAt,
                item.Disabled,
                item.CreatedAt,
                item.LastSeenAt,
                item.MonthlyChars)).ToArray(),
            total,
            take,
            skip));
    }

    private static IResult Disable(string email, AccountStore store, ILoggerFactory loggerFactory) =>
        SetDisabled(email, true, store, loggerFactory);

    private static IResult Enable(string email, AccountStore store, ILoggerFactory loggerFactory) =>
        SetDisabled(email, false, store, loggerFactory);

    private static IResult SetDisabled(
        string email,
        bool disabled,
        AccountStore store,
        ILoggerFactory loggerFactory)
    {
        var user = store.FindUserByEmail(email);
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        store.SetDisabled(user.Id, disabled);
        if (disabled)
        {
            store.RevokeAllRefreshTokens(user.Id);
        }

        // 不记录 token / 密码；只记录动作与目标邮箱，供单操作员事后对账。
        loggerFactory.CreateLogger("Admin").LogInformation(
            "admin action={Action} target={Email} result=ok",
            disabled ? "account_disabled" : "account_enabled",
            user.Email);
        return Results.Ok(new MessageResponse(disabled ? "account_disabled" : "account_enabled"));
    }

    private static IResult Diagnostics(GatewayDiagnostics diagnostics, ServerDatabase database)
    {
        var gateway = diagnostics.SnapshotGateway();
        var speech = diagnostics.SnapshotSpeech();
        var (size, wal) = database.ReadFileSizes();
        return Results.Ok(new AdminDiagnosticsResponse(
            diagnostics.StartedAtUnix,
            Math.Max(0, database.NowUnix - diagnostics.StartedAtUnix),
            ToCounters(gateway),
            ToCounters(speech),
            new AdminDatabaseSize(size, wal)));
    }

    private static AdminDiagnosticsCounters ToCounters(GatewayDiagnosticsSnapshot snapshot) =>
        new(
            snapshot.Requests,
            snapshot.Failures,
            snapshot.ByReason,
            snapshot.LastFailureAtUnix == 0 ? null : snapshot.LastFailureAtUnix,
            snapshot.LastFailureReason);

    private static IResult SetPlan(
        string email,
        [FromBody] PlanChangeRequest request,
        AccountStore store,
        ServerDatabase database,
        ILoggerFactory loggerFactory)
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
        loggerFactory.CreateLogger("Admin").LogInformation(
            "admin action=plan_changed target={Email} plan={Plan} days={Days}",
            user.Email,
            Plans.ToWire(plan),
            request.Days);
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
        ServerDatabase database,
        ILoggerFactory loggerFactory)
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
        // 只记批次元数据；码本身是 bearer instrument，不能进日志。
        loggerFactory.CreateLogger("Admin").LogInformation(
            "admin action=codes_created plan={Plan} days={Days} count={Count}",
            Plans.ToWire(plan),
            days,
            count);
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
