using System.Security.Claims;
using FastTranslation.Server.Auth;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// Account endpoints. Every failure returns a stable machine-readable token so the WPF client can
/// localize messages itself instead of parsing prose from the server.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Fallback fingerprint for clients too old to send one. Constant so the device row is reused.</summary>
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

        // Uniform failure: never reveal whether the address is registered.
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

        // Rotation keeps only one live refresh token per login chain.
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
    /// An expired subscription reports as free: the client must not keep a paid label while the
    /// server enforces free limits, or the user sees "标准版" next to a 10,000-character cap.
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

    /// <summary>A client that cannot supply a fingerprint still gets a stable device row.</summary>
    private static string DeviceFingerprint(string? fingerprint)
        => string.IsNullOrWhiteSpace(fingerprint) ? AnonymousFingerprint : fingerprint.Trim();
}
