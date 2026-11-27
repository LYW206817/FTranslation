using System.Buffers.Binary;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 代理网关。刻意做成 OpenAI 兼容端点的形态
/// （<c>POST /v1/chat/completions</c>），这样现有桌面端 provider 无需改协议：
/// 它把 <c>QwenMtOptions.BaseUrl</c> 指向 <c>{server}/v1</c>，并把 access token 发到原先发
/// API key 的位置（设计 §5 阶段二）。
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>旧客户端用于设备列表展示的指纹头。</summary>
    public const string DeviceHeaderName = "X-FT-Device";
    public const string RequestIdHeaderName = "X-FT-Request-Id";
    public const string RequestOutcomeHeaderName = "X-FT-Request-Outcome";
    private static readonly object OperationKey = new();
    private static readonly string[] GlossaryKeys = ["terms", "tm_list", "domains"];

    // v130: 绝对上限之外再按输入长度收紧。翻译输出通常与输入同量级，给 2 倍余量；
    // 极短文本至少留 64 token，否则「你好」这类请求会被自己的长度上限误杀。
    private const int OutputTokensPerInputChar = 2;

    private const int MinOutputTokenAllowance = 64;
    private const int MaxScreenshotSide = 8_192;
    private const int OcrOutputTokens = 4_096;
    private const int OcrTokenSide = 32;

    private readonly record struct Reservation(Guid UserId, Guid DeviceId, Guid QuotaId, long EstimatedCostMicros, string Source);
    private readonly record struct BillableUsage(int Chars, string Model, int TokensIn, int TokensOut, long CostMicros);

    internal enum BodyReadStatus { Ok, TooLarge, TimedOut }

    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1").WithTags("gateway").RequireAuthorization()
            .AddEndpointFilter(LimitBeforeReadingAsync);

        group.MapPost("/chat/completions", ChatCompletions);
        group.MapPost("/image", Image);
        group.MapPost("/ocr", Ocr);
    }

    private static async ValueTask<object?> LimitBeforeReadingAsync(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var services = context.RequestServices;
        var gateway = services.GetRequiredService<GatewayOptions>();
        var diagnostics = services.GetRequiredService<GatewayDiagnostics>();
        diagnostics.RecordGatewayRequest();
        if (!gateway.IsConfigured)
        {
            return Fail(diagnostics, StatusCodes.Status503ServiceUnavailable, "gateway_disabled");
        }

        // v199: 大请求在读取与解码前就需要占用名额，否则账号限频保护不到读包的内存。
        var limiter = services.GetRequiredService<GatewayRateLimiter>();
        var capacity = services.GetRequiredService<GatewayRequestCapacity>();
        var userCapacity = services.GetRequiredService<GatewayUserCapacity>();
        var userId = context.User.GetUserId();
        if (!limiter.TryAcquire(userId, gateway.RequestsPerMinute))
        {
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "rate_limited");
        }
        // v210: 单账号并发名额先于全站名额，洪水账号只能占自己那一小份；
        // 全站名额被别处占满时不把该账号的计数带出去，所以两者失败都要回滚。
        if (!userCapacity.TryEnter(userId, gateway.MaxConcurrentPerUser))
        {
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "rate_limited");
        }
        if (!capacity.TryEnter())
        {
            userCapacity.Exit(userId);
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        try
        {
            if (context.Request.Headers.TryGetValue(RequestIdHeaderName, out var identifiers))
            {
                if (identifiers.Count != 1 || !Guid.TryParseExact(identifiers[0], "D", out var id) || id == Guid.Empty)
                {
                    return Fail(diagnostics, StatusCodes.Status400BadRequest, "request_id_invalid");
                }
                var key = GatewaySettlementStore.GetOperationKey(context.User.GetUserId(), id);
                if (services.GetRequiredService<GatewaySettlementStore>().HasOperation(key))
                {
                    return AlreadyProcessed(context, diagnostics);
                }
                context.Items[OperationKey] = key;
            }
            return await next(invocation);
        }
        finally
        {
            capacity.Exit();
            userCapacity.Exit(userId);
        }
    }

    private static async Task<IResult> ChatCompletions(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayBudgetGuard budget,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        var (body, bodyStatus) = await ReadBodyAsync(context, gateway.MaxChatBodyBytes, gateway.BodyReadTimeoutSeconds, ct);
        if (bodyStatus == BodyReadStatus.TooLarge)
        {
            return Fail(diagnostics, StatusCodes.Status413PayloadTooLarge, "body_too_large");
        }
        if (bodyStatus == BodyReadStatus.TimedOut)
        {
            return Fail(diagnostics, StatusCodes.Status408RequestTimeout, "body_timeout");
        }
        if (ReadObject(body) is not { } root)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        var model = ReadString(root, "model") ?? string.Empty;
        var message = ReadChatText(root);
        var chars = message?.Length ?? 0;
        if (chars <= 0)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "text_required");
        }

        if (chars > gateway.MaxRequestChars)
        {
            return Fail(diagnostics, StatusCodes.Status413PayloadTooLarge, "text_too_long");
        }

        if (!Pricing.IsChatModel(model))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "model_not_allowed");
        }

        if (!TryReadMaxTokens(root, out var maxTokens))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        // v199: 省略长度时上游使用模型自身上限，因此也必须写入服务端算出的输出上限。
        var outputLimit = Math.Min(gateway.MaxOutputTokens,
            Math.Max(MinOutputTokenAllowance, chars * OutputTokensPerInputChar));
        if (maxTokens > outputLimit)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "max_tokens_too_large");
        }

        var outputTokens = maxTokens > 0 ? maxTokens : outputLimit;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = message } },
            ["max_tokens"] = outputTokens,
            ["stream"] = false,
        };
        var estimateChars = Encoding.UTF8.GetByteCount(message ?? string.Empty);
        var chargeChars = chars;
        if (root.TryGetProperty("translation_options", out var translationOptions))
        {
            if (translationOptions.ValueKind != JsonValueKind.Object)
            {
                return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
            }
            var options = translationOptions.EnumerateObject()
                .Where(property => property.Name is "source_lang" or "target_lang" or "terms" or "tm_list" or "domains")
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            payload["translation_options"] = options;
            estimateChars += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(options));
            // v210: 术语表/翻译记忆随请求发给上游并按输入计费，但原实现只按正文长度扣额度：
            // 1 字符正文夹带 240 KB 术语即可用近乎为零的额度烧掉整桶预算。
            chargeChars += GlossaryChars(translationOptions);
        }
        var refusal = Intent(context, principal, store, database, gateway, budget, diagnostics,
            Pricing.QuotaChars(model, chargeChars), Pricing.EstimateTextCostMicros(model, estimateChars, outputTokens), out var reservation);
        if (refusal is not null)
        {
            return refusal;
        }

        return await ForwardAsync(context, reservation, store, database, budget, diagnostics,
            () => upstream.SendChatAsync(JsonSerializer.Serialize(payload), CancellationToken.None),
            response =>
            {
                var tokensIn = EstimateInputTokens(estimateChars);
                var tokensOut = 0L;
                if (DashScopeGateway.TryReadUsage(response.Body, out var reportedIn, out var reportedOut))
                {
                    tokensIn = reportedIn;
                    tokensOut = reportedOut;
                }
                return Measure(model, Pricing.QuotaChars(model, chargeChars), tokensIn, tokensOut);
            }, ct);
    }

    /// <summary>
    /// 用同一把 Key 做截图翻译。上游 API 对图像不报告用量，所以
    /// 一次请求即一张计费图片，额度侧按固定折算值扣费（见
    /// <see cref="GatewayOptions.ImageQuotaChars"/>）。截图流程会同时调用本端点与
    /// <c>/v1/ocr</c>，两个 1,000 合起来就是产品口径的「每张截图 2,000 字符起」。
    /// </summary>
    private static async Task<IResult> Image(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayBudgetGuard budget,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        var (body, bodyStatus) = await ReadBodyAsync(context, gateway.MaxImageBodyBytes, gateway.BodyReadTimeoutSeconds, ct);
        if (bodyStatus == BodyReadStatus.TooLarge)
        {
            return Fail(diagnostics, StatusCodes.Status413PayloadTooLarge, "body_too_large");
        }
        if (bodyStatus == BodyReadStatus.TimedOut)
        {
            return Fail(diagnostics, StatusCodes.Status408RequestTimeout, "body_timeout");
        }
        if (body is null || ReadObject(body) is not { } root)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        // 图片请求必须落到价目表里的图像模型；客户端省略时用图像默认，
        // 不能回落到 gateway.Model（那是文本模型）。
        var model = ReadString(root, "model") ?? Pricing.DefaultImageModel;
        if (!Pricing.IsImageModel(model))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "model_not_allowed");
        }
        if (!root.TryGetProperty("input", out var input)
            || ReadString(input, "image_url") is not { } imageUrl || !TryReadPngSize(imageUrl, out _, out _))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }
        var payload = JsonSerializer.Serialize(new
        {
            model,
            input = new
            {
                image_url = imageUrl,
                source_lang = ReadString(input, "source_lang") ?? "auto",
                target_lang = ReadString(input, "target_lang"),
            },
        });

        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        var refusal = Intent(context, principal, store, database, gateway, budget, diagnostics,
            quotaChars, Pricing.ImageCostMicros(model), out var reservation);
        if (refusal is not null)
        {
            return refusal;
        }

        return await ForwardAsync(context, reservation, store, database, budget, diagnostics,
            () => upstream.SendImageAsync(payload, CancellationToken.None),
            _ => new BillableUsage(quotaChars, model, 0, 0, Pricing.ImageCostMicros(model)), ct);
    }

    /// <summary>
    /// 截图 OCR。由网关为一张 PNG 截图构造多模态请求；与
    /// <see cref="ChatCompletions"/> 的区别在于载荷是一张图而不是文本，因此没有
    /// 需要管制的字符上限，额度按一次截图扣费（与
    /// <see cref="Image"/> 相同折算）。但成本是真实的 token 记账：模型按视觉 token 计费，
    /// 即便额度侧是以字符为单位思考的。
    /// </summary>
    private static async Task<IResult> Ocr(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromServices] GatewayOptions gateway,
        [FromServices] AccountStore store,
        [FromServices] ServerDatabase database,
        [FromServices] GatewayBudgetGuard budget,
        [FromServices] GatewayDiagnostics diagnostics,
        [FromServices] DashScopeGateway upstream,
        CancellationToken ct)
    {
        var (body, bodyStatus) = await ReadBodyAsync(context, gateway.MaxImageBodyBytes, gateway.BodyReadTimeoutSeconds, ct);
        if (bodyStatus == BodyReadStatus.TooLarge)
        {
            return Fail(diagnostics, StatusCodes.Status413PayloadTooLarge, "body_too_large");
        }
        if (bodyStatus == BodyReadStatus.TimedOut)
        {
            return Fail(diagnostics, StatusCodes.Status408RequestTimeout, "body_timeout");
        }
        if (body is null || ReadObject(body) is not { } root)
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }

        var configuredModel = string.IsNullOrWhiteSpace(gateway.OcrModel) ? "qwen-vl-ocr" : gateway.OcrModel.Trim();
        // 价目表和上游必须使用同一个模型，包括客户端没有显式填写的情况。
        var requestedModel = ReadString(root, "model");
        var model = requestedModel ?? configuredModel;
        if (requestedModel is { } requested
            && !Pricing.IsOcrModel(requested)
            && !string.Equals(requested, configuredModel, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "model_not_allowed");
        }
        if (ReadOcrImage(root) is not { } image || !TryReadPngSize(image, out var width, out var height))
        {
            return Fail(diagnostics, StatusCodes.Status400BadRequest, "body_invalid");
        }
        var payload = JsonSerializer.Serialize(new
        {
            model,
            input = new { messages = new[] { new { role = "user", content = new[] { new { image } } } } },
            parameters = new { max_tokens = OcrOutputTokens },
        });

        // 从额度角度看，一次 OCR 与一次图片翻译定价相同：两者都是输入一张图。
        // 截图流程会同时调用 /v1/image，两个 1,000 合起来就是「每张截图 2,000 字符起」。
        var quotaChars = gateway.ImageQuotaChars > 0 ? gateway.ImageQuotaChars : 1000;
        // 每边向上取整后估计视觉块，再留出固定的请求开销。额度仍按截图一次计算。
        var estimatedInput = Math.Max(quotaChars,
            ((width + OcrTokenSide - 1) / OcrTokenSide) * ((height + OcrTokenSide - 1) / OcrTokenSide) + 64);
        var refusal = Intent(context, principal, store, database, gateway, budget, diagnostics,
            quotaChars, Pricing.TextCostMicros(model, estimatedInput, OcrOutputTokens), out var reservation);
        if (refusal is not null)
        {
            return refusal;
        }

        return await ForwardAsync(context, reservation, store, database, budget, diagnostics,
            () => upstream.SendOcrAsync(payload, CancellationToken.None),
            response =>
            {
                var tokensIn = (long)estimatedInput;
                var tokensOut = 0L;
                if (DashScopeGateway.TryReadVisionUsage(response.Body, out var reportedIn, out var reportedOut) && reportedIn > 0)
                {
                    tokensIn = reportedIn;
                    tokensOut = reportedOut;
                }
                return Measure(model, quotaChars, tokensIn, tokensOut);
            }, ct);
    }

    private static BillableUsage Measure(string model, int chars, long tokensIn, long tokensOut) =>
        new(chars, model, (int)Math.Min(tokensIn, int.MaxValue), (int)Math.Min(tokensOut, int.MaxValue),
            Pricing.TextCostMicros(model, tokensIn, tokensOut));

    private static async Task<IResult> ForwardAsync(HttpContext context, Reservation reservation,
        AccountStore accounts, ServerDatabase database, GatewayBudgetGuard budget, GatewayDiagnostics diagnostics,
        Func<Task<GatewayResponse>> send, Func<GatewayResponse, BillableUsage> measure, CancellationToken ct)
    {
        var ledger = context.RequestServices.GetRequiredService<GatewaySettlementStore>();
        var sent = false;
        var finished = false;
        string? upstreamRequestId = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            // 客户端取消与下游发送之间以这次持久化为界，避免重启后再次发送同一请求。
            sent = budget.TransferReservation(reservation.Source, reservation.EstimatedCostMicros,
                () => ledger.TryBegin(reservation.QuotaId, context.Items[OperationKey] as string,
                    reservation.Source, reservation.EstimatedCostMicros));
            if (!sent)
            {
                return AlreadyProcessed(context, diagnostics);
            }

            var response = await send();
            upstreamRequestId = response.UpstreamRequestId;
            if (response.Succeeded)
            {
                var usage = measure(response);
                finished = accounts.RecordUsage(reservation.UserId, reservation.DeviceId, usage.Chars, usage.Model,
                    usage.TokensIn, usage.TokensOut, usage.CostMicros, database.NowUnix, reservation.Source,
                    reservationId: reservation.QuotaId,
                    gatewayCompletion: new GatewayUsageCompletion(reservation.QuotaId, upstreamRequestId));
                if (!finished)
                {
                    return PendingOutcome(context, diagnostics, StatusCodes.Status502BadGateway, string.Empty);
                }
            }
            else if (response.OutcomeUnknown)
            {
                return PendingOutcome(context, diagnostics, response.StatusCode, response.Body);
            }
            else
            {
                ledger.Reject(reservation.QuotaId);
                finished = true;
                diagnostics.RecordGatewayFailure($"upstream_{response.StatusCode}");
            }
            return Results.Content(response.Body, "application/json", statusCode: response.StatusCode);
        }
        finally
        {
            try
            {
                if (sent && !finished) ledger.MarkPending(reservation.QuotaId, upstreamRequestId);
            }
            finally
            {
                try { accounts.ReleaseQuota(reservation.QuotaId); }
                finally
                {
                    if (!sent) budget.Release(reservation.Source, reservation.EstimatedCostMicros);
                }
            }
        }
    }

    private static IResult AlreadyProcessed(HttpContext context, GatewayDiagnostics diagnostics)
    {
        context.Response.Headers[RequestOutcomeHeaderName] = "already_processed";
        return Fail(diagnostics, StatusCodes.Status409Conflict, "request_already_processed");
    }

    private static IResult PendingOutcome(HttpContext context, GatewayDiagnostics diagnostics, int statusCode, string body)
    {
        context.Response.Headers[RequestOutcomeHeaderName] = "pending";
        diagnostics.RecordGatewayFailure("upstream_outcome_unknown");
        return Results.Content(body, "application/json", statusCode: statusCode);
    }

    /// <summary>
    /// 请求必须通过的每一道闸门，顺序保证不会花掉无法计费的钱：
    /// 限流 → 部署预算 → 账号额度。请求可继续时返回 null。
    /// </summary>
    private static IResult? Intent(
        HttpContext context,
        ClaimsPrincipal principal,
        AccountStore store,
        ServerDatabase database,
        GatewayOptions gateway,
        GatewayBudgetGuard budget,
        GatewayDiagnostics diagnostics,
        int chars,
        long estimatedCostMicros,
        out Reservation reservation)
    {
        reservation = default;
        var userId = principal.GetUserId();

        // v191: 免费用户把日预算烧穿后，已付费的会员也跟着 429——两类账号共用一个桶。
        // 现在按「有效档位」分桶（订阅到期的与免费同桶，和额度口径一致）；档位要先于预算闸门
        // 读出，所以用户查询提前了，闸门顺序（限流 → 预算 → 禁用 → 额度）不变。
        var user = store.FindUserById(userId);
        var tier = user is null
            ? PlanTier.Free
            : Entitlement.EffectiveTier(user.Plan, user.PlanExpiresAt, database.NowUnix);
        var (source, budgetMicros) = tier == PlanTier.Free
            ? (GatewayBuckets.Free, gateway.DailyBudgetMicros)
            : (GatewayBuckets.Paid, gateway.PaidDailyBudgetMicros);
        if (!budget.TryReserve(source, budgetMicros, estimatedCostMicros))
        {
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, "budget_exhausted");
        }

        if (user is null || user.Disabled)
        {
            budget.Release(source, estimatedCostMicros);
            return Fail(diagnostics, StatusCodes.Status403Forbidden, "account_disabled");
        }

        var deviceId = principal.GetDeviceId();
        Guid quotaId;
        QuotaPolicy.Decision decision;
        try
        {
            decision = store.TryReserveQuota(userId, deviceId, chars, tier, out quotaId);
        }
        catch (Exception)
        {
            // 预留失败也要退回预算占用，否则一次数据库异常会让预算闸门永久少一截。
            budget.Release(source, estimatedCostMicros);
            throw;
        }

        if (!decision.Allowed)
        {
            budget.Release(source, estimatedCostMicros);
            // 故意用 429（而不是 403）：客户端会把它读作“额度用尽”并静默切换到
            // 用户自己的 Key，于是套餐耗尽只是让产品降级，而不是把它弄坏。
            context.Response.Headers["X-FT-Quota-Reason"] = decision.Reason ?? "quota_exhausted";
            return Fail(diagnostics, StatusCodes.Status429TooManyRequests, decision.Reason ?? "quota_exhausted");
        }

        reservation = new Reservation(userId, deviceId, quotaId, estimatedCostMicros, source);
        return null;
    }

    private static JsonElement? ReadObject(string? body)
    {
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadChatText(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() != 1 || ReadString(messages[0], "role") != "user")
        {
            return null;
        }

        return ReadString(messages[0], "content");
    }

    private static string? ReadOcrImage(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() != 1 || ReadString(messages[0], "role") != "user"
            || !messages[0].TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() != 1)
        {
            return null;
        }
        return ReadString(content[0], "image") ?? ReadString(content[0], "image_url");
    }

    private static bool TryReadPngSize(string value, out int width, out int height)
    {
        width = height = 0;
        const string prefix = "data:image/png;base64,";
        Span<byte> header = stackalloc byte[24];
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length < prefix.Length + 32
            || !Convert.TryFromBase64Chars(value.AsSpan(prefix.Length, 32), header, out var written)
            || written != header.Length || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !header[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }
        // 只读头部即可估算预算，无需在服务端解码整幅大图。
        width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return width is > 0 and <= MaxScreenshotSide && height is > 0 and <= MaxScreenshotSide;
    }

    /// <summary>缺少上游用量时补入固定的前导 Token，避免低估短句成本。</summary>
    private static long EstimateInputTokens(int chars) => chars + 45L;

    private static bool TryReadMaxTokens(JsonElement root, out int tokens)
    {
        tokens = 0;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            {
                return false;
            }
            tokens = Math.Max(tokens, count);
        }
        return true;
    }

    /// <summary>
    /// 计入额度的术语表字符数。只数 terms/tm_list/domains 的序列化内容：
    /// source_lang/target_lang 是固定短串，计进去会让每个普通请求都多扣几十字符。
    /// 按序列化长度而不是逐字段之和，空字符串洪泛也会按 JSON 结构如实计入。
    /// </summary>
    private static int GlossaryChars(JsonElement translationOptions)
    {
        var total = 0;
        foreach (var name in GlossaryKeys)
        {
            if (translationOptions.TryGetProperty(name, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                total += JsonSerializer.Serialize(value).Length;
            }
        }
        return total;
    }

    private static async Task<(string? Body, BodyReadStatus Status)> ReadBodyAsync(
        HttpContext context,
        int maxBytes,
        int timeoutSeconds,
        CancellationToken ct)
    {
        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return (null, BodyReadStatus.TooLarge);
        }

        return await ReadBodyWithTimeoutAsync(context.Request.Body, maxBytes, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 读请求体的核心逻辑，独立出来是为了能用一条慢速流直接测超时（TestServer 会先缓冲整包，
    /// 走 HTTP 测不到滴流）。
    /// </summary>
    internal static async Task<(string? Body, BodyReadStatus Status)> ReadBodyWithTimeoutAsync(
        Stream body,
        int maxBytes,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // 不用 ReadToEnd：Content-Length 可以缺失（chunked），必须在读的过程中就截断，
        // 否则一个声明不了长度的大请求仍会把整份 body 读进内存。
        await using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        // v210: 慢速滴流能让一条请求占着并发名额近半小时，到点就放弃。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (null, BodyReadStatus.TooLarge);
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, BodyReadStatus.TimedOut);
        }

        if (buffer.Length == 0)
        {
            return (null, BodyReadStatus.Ok);
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return (string.IsNullOrWhiteSpace(text) ? null : text, BodyReadStatus.Ok);
    }

    private static IResult Fail(GatewayDiagnostics diagnostics, int status, string error)
    {
        diagnostics.RecordGatewayFailure(error);
        return Results.Json(new ErrorResponse(error), statusCode: status);
    }
}
