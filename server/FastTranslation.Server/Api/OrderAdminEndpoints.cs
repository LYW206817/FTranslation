using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FastTranslation.Server.Api;

/// <summary>
/// 订单审核面。确认收款是人工动作——个人收款码没有支付回调，服务端只能等运营
/// 核对该交易单号后点确认；确认本身是幂等的，重复点击不会重复加套餐。
/// </summary>
public static class OrderAdminEndpoints
{
    public static void MapOrderAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("admin")
            .RequireRateLimiting(RateLimiting.AdminPolicy);

        group.MapGet("/orders", List).AddEndpointFilter(AdminEndpoints.RequireAdminToken);
        group.MapPost("/orders/{id:guid}/confirm", Confirm).AddEndpointFilter(AdminEndpoints.RequireAdminToken);
        group.MapPost("/orders/{id:guid}/cancel", Cancel).AddEndpointFilter(AdminEndpoints.RequireAdminToken);
    }

    private static IResult List(
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        OrderStore orders)
    {
        var state = string.IsNullOrWhiteSpace(status) ? OrderStatus.PendingReview : status.Trim().ToLowerInvariant();
        if (state != "all" && !OrderStatus.IsKnown(state))
        {
            return Results.BadRequest(new ErrorResponse("status_invalid"));
        }

        var take = limit is > 0 and <= 200 ? limit.Value : 50;
        var skip = offset is > 0 ? offset.Value : 0;
        var (rows, total) = orders.ListForAdmin(state, take, skip);
        var items = rows.Select(row => new AdminOrderDto(
            row.Id,
            row.Email,
            Plans.ToWire(row.Plan),
            row.Cycle,
            row.Days,
            row.AmountCents,
            row.Status,
            row.PaymentMethod,
            row.PaymentRef,
            row.CreatedAt,
            row.SubmittedAt,
            row.ConfirmedAt,
            row.CancelledAt,
            row.CancelReason)).ToArray();
        return Results.Ok(new AdminOrderPageResponse(items, total, take, skip));
    }

    private static IResult Confirm(
        Guid id,
        OrderStore orders,
        ServerDatabase database,
        ILoggerFactory loggers)
    {
        if (orders.Find(id) is null) return Results.NotFound(new ErrorResponse("order_missing"));
        var result = orders.Confirm(id, database.NowUnix);
        if (result == OrderStore.ConfirmResult.PlanMismatch)
        {
            return Results.Conflict(new ErrorResponse("order_plan_mismatch"));
        }
        if (result != OrderStore.ConfirmResult.Ok)
        {
            return Results.Conflict(new ErrorResponse("order_not_pending"));
        }

        // 不写交易单号：它是资金凭据，日志会被长期保留。
        loggers.CreateLogger("Admin").LogInformation(
            "admin action=order_confirmed order={OrderId} result=ok", id);
        return Results.Ok(new MessageResponse("order_confirmed"));
    }

    private static IResult Cancel(
        Guid id,
        [FromBody] CancelOrderRequest request,
        OrderStore orders,
        ServerDatabase database,
        ILoggerFactory loggers)
    {
        var order = orders.Find(id);
        if (order is null) return Results.NotFound(new ErrorResponse("order_missing"));
        if (!OrderStatus.IsActive(order.Status))
        {
            return Results.Conflict(new ErrorResponse("order_not_pending"));
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "admin_cancelled" : request.Reason.Trim();
        if (reason.Length > 80) reason = reason[..80];
        if (!orders.Cancel(id, null, allowReview: true, reason, database.NowUnix))
        {
            return Results.Conflict(new ErrorResponse("order_not_pending"));
        }

        loggers.CreateLogger("Admin").LogInformation(
            "admin action=order_cancelled order={OrderId} result=ok", id);
        return Results.Ok(new MessageResponse("order_cancelled"));
    }
}
