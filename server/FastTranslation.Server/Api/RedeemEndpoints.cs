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

        // Same tier stacks onto the days still left; a different tier starts today, because the old
        // end date was bought for a plan the user no longer has.
        var stacks = user.Plan == record.Plan && user.PlanExpiresAt is { } current && current > now;
        var expiresAt = (stacks ? user.PlanExpiresAt!.Value : now) + (record.Days * SecondsPerDay);
        accounts.SetPlan(user.Id, record.Plan, expiresAt);

        var updated = accounts.FindUserById(user.Id)!;
        var usage = accounts.ReadUsage(user.Id, null);
        return Results.Ok(new RedeemResponse(
            AuthEndpoints.ToAccountDto(updated, database),
            AuthEndpoints.ToUsageDto(usage, record.Plan)));
    }
}
