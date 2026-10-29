using System.Security.Claims;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// The one endpoint a paying customer touches before they have a plan: turning a code they bought
/// into days of subscription. Kept away from <see cref="AccountEndpoints"/> because it is the only
/// place where the account row is mutated on the user's own behalf.
/// </summary>
public static class RedeemEndpoints
{
    private const long SecondsPerDay = 86_400L;

    public static void MapRedeemEndpoints(this IEndpointRouteBuilder app)
    {
        // Same IP-partitioned limiter as sign-in: a wrong code must not be a free oracle for
        // guessing, and the form posts one request per click anyway.
        app.MapPost("/api/v1/redeem", Redeem)
            .WithTags("account")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.AuthPolicy);
    }

    private static IResult Redeem(
        [FromBody] RedeemRequest request,
        ClaimsPrincipal principal,
        AccountStore accounts,
        RedeemStore codes,
        ServerDatabase database)
    {
        var typed = RedeemCode.Normalize(request.Code);
        if (typed.Length == 0)
        {
            return Results.BadRequest(new ErrorResponse("redeem_code_invalid"));
        }

        // Rows are keyed by the printed form so the operator can grep a code they pasted into the
        // database; what the user typed is folded back into that same canonical shape.
        var code = RedeemCode.Format(typed);

        var user = accounts.FindUserById(principal.GetUserId());
        if (user is null)
        {
            return Results.NotFound(new ErrorResponse("account_missing"));
        }

        if (user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        var record = codes.Find(code);
        if (record is null)
        {
            return Results.NotFound(new ErrorResponse("redeem_code_invalid"));
        }

        if (record.IsRedeemed)
        {
            return Results.Conflict(new ErrorResponse("redeem_code_used"));
        }

        // 410, not 404: the code existed and was cancelled. Calling it "wrong code" would send the
        // buyer back to whoever sold it with no way to prove they typed it correctly.
        if (record.IsRevoked)
        {
            return Results.Json(new ErrorResponse("redeem_code_revoked"), statusCode: StatusCodes.Status410Gone);
        }

        var now = database.NowUnix;

        // Refuse to make the customer worse off: a cheaper code must not shorten a running
        // subscription. The code is checked before it is claimed, so it stays usable afterwards.
        if (Entitlement.IsSubscriptionActive(user.Plan, user.PlanExpiresAt, now) && user.Plan > record.Plan)
        {
            return Results.BadRequest(new ErrorResponse("redeem_plan_downgrade"));
        }

        if (!codes.TryClaim(code, user.Id, now))
        {
            // Somebody else redeemed it between the read above and this claim.
            return Results.Conflict(new ErrorResponse("redeem_code_used"));
        }

        // Pay out from wherever the customer stands. A downgrade is refused above, so every redeem
        // that gets here is same-tier or an upgrade — and resetting to "today" on an upgrade would
        // make a buyer pay twice for the days they already own.
        long from = now;
        if (user.PlanExpiresAt is { } current && current > now)
        {
            from = current;
        }

        var expiresAt = from + (record.Days * SecondsPerDay);
        accounts.SetPlan(user.Id, record.Plan, expiresAt);

        var updated = accounts.FindUserById(user.Id)!;
        var usage = accounts.ReadUsage(user.Id, null);
        return Results.Ok(new RedeemResponse(
            AuthEndpoints.ToAccountDto(updated, database),
            AuthEndpoints.ToUsageDto(usage, record.Plan)));
    }
}

/// <summary>
/// The operator side of the same ledger. A minted batch is returned exactly once, so without
/// these two routes a mistyped batch is unrecoverable and a leaked one is unkillable.
/// </summary>
public static class RedeemAdminEndpoints
{
    public static void MapRedeemAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("admin");
        group.MapGet("/codes", ListCodes).AddEndpointFilter(AdminEndpoints.RequireAdminToken);
        group.MapPost("/codes/{code}/revoke", RevokeCode).AddEndpointFilter(AdminEndpoints.RequireAdminToken);
    }

    private static IResult ListCodes(
        RedeemStore codes,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        var state = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
        if (!RedeemStore.IsKnownStatus(state))
        {
            return Results.BadRequest(new ErrorResponse("status_invalid"));
        }

        // A ledger listing needs a bound: an operator paging a few hundred rows never wants 200
        // of them at once, and an unbounded SELECT is a free way to hang the process.
        var take = limit is > 0 and <= 200 ? limit.Value : 50;
        var skip = offset is > 0 ? offset.Value : 0;
        var items = codes.List(state, take, skip)
            .Select(row => new RedeemCodeDto(
                row.Code,
                row.Plan,
                row.Days,
                row.Note,
                row.CreatedAt,
                row.RedeemedEmail,
                row.RedeemedAt,
                row.RevokedAt))
            .ToArray();

        return Results.Ok(new RedeemCodePageResponse(items, codes.Count(state), take, skip));
    }

    private static IResult RevokeCode(
        [FromRoute] string code,
        RedeemStore codes,
        ServerDatabase database)
    {
        var typed = RedeemCode.Normalize(code);
        if (typed.Length == 0)
        {
            return Results.BadRequest(new ErrorResponse("redeem_code_invalid"));
        }

        var printed = RedeemCode.Format(typed);
        if (codes.Revoke(printed, database.NowUnix))
        {
            return Results.Ok(new MessageResponse("code_revoked"));
        }

        // The update matched nothing, so say why: unknown, already paid out, or already dead.
        return codes.Find(printed) switch
        {
            null => Results.NotFound(new ErrorResponse("code_missing")),
            { IsRevoked: true } => Results.Ok(new MessageResponse("code_revoked")),
            _ => Results.Conflict(new ErrorResponse("code_already_redeemed")),
        };
    }
}
