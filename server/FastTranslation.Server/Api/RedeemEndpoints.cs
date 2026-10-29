using System.Security.Claims;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// 付费客户在还没有套餐时唯一会碰的端点：把买到的码换成
/// 若干天订阅。它没有放进 <see cref="AccountEndpoints"/>，因为这是唯一
/// 由用户自行改动账号行的地方。
/// </summary>
public static class RedeemEndpoints
{
    private const long SecondsPerDay = 86_400L;

    public static void MapRedeemEndpoints(this IEndpointRouteBuilder app)
    {
        // 与登录相同的按 IP 分区限流器：错误的码不能变成免费的爆破预言机，
        // 而且表单本来也是点一次发一次。
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

        // 数据行以打印形态为键，这样运维能把粘进数据库的码直接 grep 出来；
        // 用户输入的内容会被归一成同一种规范形态。
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

        // 用 410 而非 404：码存在但被作废了。若报成“码错误”，会让买家
        // 回去找卖家，却无法证明自己输入无误。
        if (record.IsRevoked)
        {
            return Results.Json(new ErrorResponse("redeem_code_revoked"), statusCode: StatusCodes.Status410Gone);
        }

        var now = database.NowUnix;

        // 不能让客户吃亏：更便宜的码不得缩短正在生效的
        // 订阅。这一步在占用码之前检查，所以码之后仍然可用。
        if (Entitlement.IsSubscriptionActive(user.Plan, user.PlanExpiresAt, now) && user.Plan > record.Plan)
        {
            return Results.BadRequest(new ErrorResponse("redeem_plan_downgrade"));
        }

        if (!codes.TryClaim(code, user.Id, now))
        {
            // 在上面的读取与这次占用之间，别人先兑换了。
            return Results.Conflict(new ErrorResponse("redeem_code_used"));
        }

        // 从客户当前所在的位置往后接。降级在上面已被拒绝，所以能走到这里的
        // 兑换都是同档或升级——而升级时重置到“今天”会让买家
        // 为已经拥有的天数重复付费。
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
/// 同一本账的运维侧。生成的一批码只会返回一次，所以没有
/// 这两个路由，录错的一批无法追回，泄露的一批也无法作废。
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

        // 账目列表必须有上限：运维翻几百行时不会想一次拿 200 行，
        // 而一个无界 SELECT 就是免费的挂进程方式。
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

        // 更新没有命中任何行，要说明原因：未知、已兑付，还是已作废。
        return codes.Find(printed) switch
        {
            null => Results.NotFound(new ErrorResponse("code_missing")),
            { IsRevoked: true } => Results.Ok(new MessageResponse("code_revoked")),
            _ => Results.Conflict(new ErrorResponse("code_already_redeemed")),
        };
    }
}
