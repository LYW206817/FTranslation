using System.Security.Claims;
using System.Text;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Orders;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// 套餐订阅订单。金额、天数与可用收款方式全部由服务端决定；客户端只传套餐和周期。
/// 付款成功没有回调，订单停在 pending_review，等运营在后台核对交易单号后确认开通。
/// </summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders").WithTags("orders")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        group.MapPost("", Create);
        group.MapGet("/current", Current);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/submit", Submit);
        group.MapPost("/{id:guid}/cancel", Cancel);
    }

    private static IResult Create(
        [FromBody] CreateOrderRequest request,
        ClaimsPrincipal principal,
        AccountStore accounts,
        OrderStore orders,
        PaymentQrProvider payments,
        ServerDatabase database)
    {
        var user = accounts.FindUserById(principal.GetUserId());
        if (user is null) return Results.NotFound(new ErrorResponse("account_missing"));
        if (user.Disabled) return Results.Forbid();

        if (!Plans.TryParse(request.Plan, out var plan) || plan == PlanTier.Free)
        {
            return Results.BadRequest(new ErrorResponse("plan_invalid"));
        }

        if (!OrderPricing.TryParseCycle(request.Cycle, out var cycle))
        {
            return Results.BadRequest(new ErrorResponse("cycle_invalid"));
        }

        if (!OrderPricing.TryPrice(plan, cycle, out var amountCents))
        {
            return Results.BadRequest(new ErrorResponse("plan_invalid"));
        }

        var now = database.NowUnix;
        var active = Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, now);
        if (active != PlanTier.Free)
        {
            // 降档会损失用户已买的时长；升级涉及人工补差价。两者都不走自助下单。
            if (plan < active) return Results.Conflict(new ErrorResponse("plan_downgrade"));
            if (plan > active) return Results.Conflict(new ErrorResponse("upgrade_requires_support"));
        }

        if (orders.FindCurrent(user.Id) is { } existing)
        {
            // 不返回旧订单的付款码：客户端拿到 409 后会再请求 /current 并展示它。
            return Results.Conflict(new ErrorResponse("order_pending"));
        }

        if (payments.Methods.Count == 0)
        {
            return Results.Json(new ErrorResponse("payment_unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var order = orders.Create(user.Id, plan, cycle, OrderPricing.Days(cycle), amountCents, now);
        return order is null
            ? Results.Conflict(new ErrorResponse("order_pending"))
            : Results.Ok(ToDto(order, payments));
    }

    private static IResult Current(ClaimsPrincipal principal, OrderStore orders, PaymentQrProvider payments)
    {
        var order = orders.FindCurrent(principal.GetUserId());
        return order is null
            ? Results.NotFound(new ErrorResponse("order_missing"))
            : Results.Ok(ToDto(order, payments));
    }

    private static IResult Get(Guid id, ClaimsPrincipal principal, OrderStore orders, PaymentQrProvider payments)
    {
        var order = orders.Find(id);
        if (order is null || order.UserId != principal.GetUserId())
        {
            return Results.NotFound(new ErrorResponse("order_missing"));
        }

        return Results.Ok(ToDto(order, payments));
    }

    private static IResult Submit(
        Guid id,
        [FromBody] SubmitOrderRequest request,
        ClaimsPrincipal principal,
        OrderStore orders,
        PaymentQrProvider payments,
        ServerDatabase database)
    {
        var order = orders.Find(id);
        if (order is null || order.UserId != principal.GetUserId())
        {
            return Results.NotFound(new ErrorResponse("order_missing"));
        }

        if (order.Status != OrderStatus.PendingPayment)
        {
            return Results.Conflict(new ErrorResponse("order_not_pending"));
        }

        var method = (request.PaymentMethod ?? string.Empty).Trim().ToLowerInvariant();
        if (!payments.TryGet(method, out _))
        {
            return Results.BadRequest(new ErrorResponse("payment_method_invalid"));
        }

        var paymentRef = NormalizePaymentRef(request.PaymentRef);
        if (paymentRef.Length is < 6 or > 64)
        {
            return Results.BadRequest(new ErrorResponse("payment_ref_invalid"));
        }

        var result = orders.Submit(id, principal.GetUserId(), method, paymentRef, database.NowUnix);
        return result switch
        {
            OrderStore.SubmitResult.DuplicateRef => Results.Conflict(new ErrorResponse("payment_ref_used")),
            OrderStore.SubmitResult.NotPending => Results.Conflict(new ErrorResponse("order_not_pending")),
            _ => Results.Ok(ToDto(orders.Find(id)!, payments)),
        };
    }

    private static IResult Cancel(
        Guid id,
        ClaimsPrincipal principal,
        OrderStore orders,
        ServerDatabase database)
    {
        var order = orders.Find(id);
        if (order is null || order.UserId != principal.GetUserId())
        {
            return Results.NotFound(new ErrorResponse("order_missing"));
        }

        if (order.Status != OrderStatus.PendingPayment)
        {
            return Results.Conflict(new ErrorResponse("order_not_pending"));
        }

        return orders.Cancel(id, principal.GetUserId(), allowReview: false, "user_cancelled", database.NowUnix)
            ? Results.Ok(new MessageResponse("order_cancelled"))
            : Results.Conflict(new ErrorResponse("order_not_pending"));
    }

    /// <summary>
    /// 账单详情整段粘贴时可能带「交易单号：」或空格；优先取最长的连续数字串，
    /// 没有足够长的数字串就保留去空白后的原文。交易单号不是密码，不在这里做格式猜测。
    /// </summary>
    internal static string NormalizePaymentRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var best = new StringBuilder();
        var current = new StringBuilder();
        foreach (var c in compact)
        {
            if (char.IsAsciiDigit(c))
            {
                current.Append(c);
                continue;
            }

            if (current.Length > best.Length) best = new StringBuilder(current.ToString());
            current.Clear();
        }

        if (current.Length > best.Length) best = new StringBuilder(current.ToString());
        return best.Length >= 12 ? best.ToString() : compact;
    }

    private static OrderDto ToDto(OrderRecord order, PaymentQrProvider payments)
    {
        var methods = payments.Methods
            .Select(qr => new PaymentMethodDto(qr.Method, qr.Label, qr.DataUrl))
            .ToArray();
        return new OrderDto(
            order.Id,
            Plans.ToWire(order.Plan),
            order.Cycle,
            order.Days,
            order.AmountCents,
            order.Status,
            methods,
            order.PaymentMethod,
            order.PaymentRef,
            order.CreatedAt,
            order.SubmittedAt,
            order.ConfirmedAt,
            order.CancelledAt,
            order.CancelReason);
    }
}
