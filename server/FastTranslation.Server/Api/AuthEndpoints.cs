using System.Security.Claims;
using FastTranslation.Server.Auth;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// 账号端点。每种失败都返回稳定的机器可读 token，让 WPF 客户端可以
/// 自行本地化文案，而不必解析服务端返回的自然语言。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>为太旧、无法上报指纹的客户端准备的兜底指纹。用常量以保证设备行被复用。</summary>
    private const string AnonymousFingerprint = "unidentified";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("auth").RequireRateLimiting(RateLimiting.AuthPolicy);

        group.MapPost("/code", RequestCode).AllowAnonymous();
        group.MapPost("/register", Register).AllowAnonymous();
        group.MapPost("/login", Login).AllowAnonymous();
        group.MapPost("/refresh", Refresh).AllowAnonymous();
        group.MapPost("/logout", Logout).RequireAuthorization();
    }

    private static async Task<IResult> RequestCode(
        [FromBody] VerificationCodeRequest request,
        VerificationCodeService codes,
        IVerificationMailSender mail,
        ServerOptions options,
        CancellationToken ct)
    {
        if (CredentialRules.ValidateEmail(request.Email) is { } emailError)
        {
            return Results.BadRequest(new ErrorResponse(emailError));
        }

        if (!options.RequireEmailVerification)
        {
            return Results.Ok(new MessageResponse("verification_disabled"));
        }

        var email = AccountStore.NormalizeEmail(request.Email);
        var issued = codes.Issue(email);
        if (!issued.Sent)
        {
            return Results.Json(
                new ErrorResponse(issued.Error!, $"retry_after_{issued.RetryAfterSeconds}"),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        await mail.SendCodeAsync(email, issued.Code!, ct);
        return Results.Ok(new MessageResponse("code_sent"));
    }

    private static IResult Register(
        [FromBody] RegisterRequest request,
        AccountStore store,
        VerificationCodeService codes,
        TokenService tokens,
        ServerOptions options,
        ServerDatabase database)
    {
        if (CredentialRules.ValidateEmail(request.Email) is { } emailError)
        {
            return Results.BadRequest(new ErrorResponse(emailError));
        }

        if (CredentialRules.ValidatePassword(request.Password) is { } passwordError)
        {
            return Results.BadRequest(new ErrorResponse(passwordError));
        }

        var email = AccountStore.NormalizeEmail(request.Email);

        if (options.RequireEmailVerification && codes.Consume(email, request.Code) is { } codeError)
        {
            return Results.BadRequest(new ErrorResponse(codeError));
        }

        if (store.FindUserByEmail(email) is not null)
        {
            return Results.Conflict(new ErrorResponse("email_taken"));
        }

        var user = store.CreateUser(email, PasswordHasher.Hash(request.Password), PlanTier.Free);
        var device = store.RegisterDevice(user.Id, DeviceFingerprint(request.DeviceFingerprint), request.DeviceName ?? string.Empty);
        if (device.Device is not { } registered)
        {
            return Results.BadRequest(new ErrorResponse(device.Error!));
        }

        return Results.Ok(IssueTokens(store, tokens, options, database, user, registered));
    }

    private static IResult Login(
        [FromBody] LoginRequest request,
        AccountStore store,
        TokenService tokens,
        ServerOptions options,
        ServerDatabase database)
    {
        var user = store.FindUserByEmail(AccountStore.NormalizeEmail(request.Email ?? string.Empty));

        // 统一失败：绝不泄露该邮箱是否已注册。
        if (user is null || !PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            return Results.Json(new ErrorResponse("credentials_invalid"), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        var device = store.RegisterDevice(user.Id, DeviceFingerprint(request.DeviceFingerprint), request.DeviceName ?? string.Empty);
        if (device.Device is not { } registered)
        {
            return Results.BadRequest(new ErrorResponse(device.Error!));
        }

        return Results.Ok(IssueTokens(store, tokens, options, database, user, registered));
    }

    private static IResult Refresh(
        [FromBody] RefreshRequest request,
        AccountStore store,
        TokenService tokens,
        ServerOptions options,
        ServerDatabase database)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.BadRequest(new ErrorResponse("refresh_token_required"));
        }

        if (store.ConsumeRefreshToken(TokenService.HashRefreshToken(request.RefreshToken)) is not { } userId)
        {
            return Results.Json(new ErrorResponse("refresh_token_invalid"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = store.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        // 轮换后每条登录链上只保留一个有效的 refresh token。
        var accessToken = tokens.CreateAccessToken(user.Id, user.Email, Plans.ToWire(user.Plan), out var expiresAt);
        var refreshToken = TokenService.CreateRefreshToken();
        store.SaveRefreshToken(user.Id, TokenService.HashRefreshToken(refreshToken), database.NowUnix + (options.RefreshTokenDays * 86_400L));

        return Results.Ok(new AuthResponse(
            accessToken,
            refreshToken,
            SecondsUntil(expiresAt, database),
            ToAccountDto(user, database),
            ToUsageDto(store.ReadUsage(user.Id, null), Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix))));
    }

    private static IResult Logout(ClaimsPrincipal principal, AccountStore store)
    {
        store.RevokeAllRefreshTokens(principal.GetUserId());
        return Results.Ok(new MessageResponse("logged_out"));
    }

    internal static AuthResponse IssueTokens(
        AccountStore store,
        TokenService tokens,
        ServerOptions options,
        ServerDatabase database,
        UserRecord user,
        DeviceRecord device)
    {
        var accessToken = tokens.CreateAccessToken(user.Id, user.Email, Plans.ToWire(user.Plan), out var expiresAt);
        var refreshToken = TokenService.CreateRefreshToken();
        store.SaveRefreshToken(user.Id, TokenService.HashRefreshToken(refreshToken), database.NowUnix + (options.RefreshTokenDays * 86_400L));

        return new AuthResponse(
            accessToken,
            refreshToken,
            SecondsUntil(expiresAt, database),
            ToAccountDto(user, database),
            ToUsageDto(store.ReadUsage(user.Id, device.Id), Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix)));
    }

    private static int SecondsUntil(DateTimeOffset expiresAt, ServerDatabase database)
        => (int)Math.Max(0, expiresAt.ToUnixTimeSeconds() - database.NowUnix);

    /// <summary>
    /// 过期订阅按免费上报：客户端不能在服务端已按免费限制的同时还显示付费标签，
    /// 否则用户会看到“标准版”旁边挂着 10,000 字符上限。
    /// </summary>
    internal static AccountDto ToAccountDto(UserRecord user, ServerDatabase database)
    {
        var tier = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        return new AccountDto(
            user.Id,
            user.Email,
            Plans.ToWire(tier),
            Plans.MonthlyQuota(tier),
            Plans.DailyQuota(tier),
            tier == PlanTier.Free ? null : user.PlanExpiresAt,
            user.DisplayName);
    }

    internal static UsageDto ToUsageDto(UsageSnapshot usage, PlanTier plan)
    {
        var quotaMonthly = Plans.MonthlyQuota(plan);
        var quotaDaily = Plans.DailyQuota(plan);
        return new UsageDto(
            usage.MonthlyChars,
            quotaDaily is null ? null : usage.DailyChars,
            quotaMonthly,
            quotaDaily,
            Math.Max(0, quotaMonthly - usage.MonthlyChars),
            quotaDaily is null ? null : Math.Max(0, quotaDaily.Value - usage.DailyChars),
            usage.Month,
            usage.Day);
    }

    /// <summary>无法提供指纹的客户端同样会拿到一条稳定的设备行。</summary>
    private static string DeviceFingerprint(string? fingerprint)
        => string.IsNullOrWhiteSpace(fingerprint) ? AnonymousFingerprint : fingerprint.Trim();
}
