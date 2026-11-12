using System.Text.Json;

namespace FastTranslation.Server.Api;

/// <summary>
/// 传输契约。字段名在传输层为 camelCase（默认 JSON 策略），它们是
/// 与 WPF 客户端之间的契约——重命名其中任何一个都会破坏客户端。
/// </summary>
public sealed record RegisterRequest(string Email, string Password, string? Code, string? DeviceFingerprint, string? DeviceName);

public sealed record LoginRequest(string Email, string Password, string? DeviceFingerprint, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken);

public sealed record VerificationCodeRequest(string Email);

public sealed record PasswordResetCodeRequest(string Email);

public sealed record PasswordResetRequest(string Email, string? Code, string? NewPassword);

public sealed record DeleteAccountRequest(string? Password, string? Confirmation);

public sealed record TokenPairResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    string TokenType = "Bearer");

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    AccountDto Account,
    UsageDto Usage,
    string TokenType = "Bearer");

/// <summary><paramref name="PlanExpiresAt"/> 为 Unix 秒；付费套餐上为 null 表示“无到期时间”。</summary>
public sealed record AccountDto(
    Guid Id,
    string Email,
    string Plan,
    int QuotaMonthly,
    int? QuotaDaily,
    long? PlanExpiresAt,
    string DisplayName = "");

/// <summary>空的 <c>DisplayName</c> 表示清除：界面随后回落到邮箱。</summary>
public sealed record UpdateProfileRequest(string? DisplayName);

/// <summary>
/// 修改密码会返回一对新 token，这样执行修改的设备保持登录，
/// 而其他所有 refresh token 都被吊销。
/// </summary>
public sealed record ChangePasswordRequest(
    string? CurrentPassword, string? NewPassword, string? DeviceFingerprint, string? DeviceName);

/// <summary><paramref name="PeriodStart"/> 是当前个人额度周期的起点日期（yyyy-MM-dd，UTC）。</summary>
public sealed record UsageDto(
    int UsedMonthlyChars,
    int? UsedDailyChars,
    int QuotaMonthlyChars,
    int? QuotaDailyChars,
    int RemainingMonthlyChars,
    int? RemainingDailyChars,
    string PeriodStart,
    string Day);

public sealed record ErrorResponse(string Error, string? Message = null);

/// <summary>订阅订单：客户端只传套餐与周期，金额、天数由服务端算。</summary>
public sealed record CreateOrderRequest(string? Plan, string? Cycle);

public sealed record SubmitOrderRequest(string? PaymentMethod, string? PaymentRef);

public sealed record CancelOrderRequest(string? Reason);

/// <summary>收款方式与二维码 data URL；客户端直接把它交给图片控件。</summary>
public sealed record PaymentMethodDto(string Method, string Label, string QrDataUrl);

public sealed record OrderDto(
    Guid Id,
    string Plan,
    string Cycle,
    int Days,
    int AmountCents,
    string Status,
    IReadOnlyList<PaymentMethodDto> PaymentMethods,
    string PaymentMethod,
    string PaymentRef,
    long CreatedAt,
    long? SubmittedAt,
    long? ConfirmedAt,
    long? CancelledAt,
    string CancelReason);

/// <summary>后台订单行带账号邮箱；交易单号只发给管理端，不在用户列表接口里额外暴露。</summary>
public sealed record AdminOrderDto(
    Guid Id,
    string Email,
    string Plan,
    string Cycle,
    int Days,
    int AmountCents,
    string Status,
    string PaymentMethod,
    string PaymentRef,
    long CreatedAt,
    long? SubmittedAt,
    long? ConfirmedAt,
    long? CancelledAt,
    string CancelReason);

public sealed record AdminOrderPageResponse(
    IReadOnlyList<AdminOrderDto> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record AdminMetricsResponse(
    int TotalUsers,
    int FreeUsers,
    int LightUsers,
    int StandardUsers,
    int MaxUsers,
    int ActiveUsersLast30Days);

/// <summary>管理端用量汇总。costMicros 为微元（1e-6 元）。</summary>
public sealed record AdminUsageResponse(
    int Days,
    long FromUnix,
    long ToUnix,
    AdminUsageTotals Total,
    IReadOnlyList<AdminUsageBySource> BySource,
    IReadOnlyList<AdminUsageByDay> ByDay,
    AdminTodayUsage Today);

public sealed record AdminUsageTotals(long Requests, long Chars, long TokensIn, long TokensOut, long CostMicros);

public sealed record AdminUsageBySource(string Source, long Requests, long Chars, long CostMicros);

public sealed record AdminUsageByDay(string Day, long Requests, long Chars, long CostMicros);

public sealed record AdminTodayUsage(long CostMicros, long BudgetMicros);

public sealed record AdminAccountDto(
    string Email,
    string Plan,
    string EffectivePlan,
    long? PlanExpiresAt,
    bool Disabled,
    long CreatedAt,
    long LastSeenAt,
    int MonthlyChars);

public sealed record AdminAccountPageResponse(IReadOnlyList<AdminAccountDto> Items, int Total, int Limit, int Offset);

public sealed record AdminDiagnosticsResponse(
    long StartedAtUnix,
    long UptimeSeconds,
    AdminDiagnosticsCounters Gateway,
    AdminDiagnosticsCounters Speech,
    AdminDatabaseSize Database);

public sealed record AdminDiagnosticsCounters(
    long Requests,
    long Failures,
    IReadOnlyDictionary<string, long> ByReason,
    long? LastFailureAtUnix,
    string? LastFailureReason);

public sealed record AdminDatabaseSize(long SizeBytes, long WalBytes);

public sealed record MessageResponse(string Message);

public sealed record DeviceRequest(string? Fingerprint, string? Name);

/// <summary><paramref name="IsCurrent"/> 按调用方送来的 X-FT-Device 指纹判定；缺头时为 false。</summary>
public sealed record DeviceDto(Guid Id, string Name, long CreatedAt, long LastSeenAt, bool IsCurrent = false);

/// <summary>设备上限由服务端下发，避免客户端写死一个会随策略变化而漂移的数字。</summary>
public sealed record DeviceListResponse(IReadOnlyList<DeviceDto> Devices, int MaxDevices);

/// <summary>
/// <paramref name="Days"/> 为 null 时保留已有的到期时间；为 0 时清除（永久授权）；
/// 正数则把到期时间设为当前时间加相应天数。
/// </summary>
public sealed record PlanChangeRequest(string? Plan, int? Days = null);

// ---- 兑换（激活码） ----

/// <summary>买家粘贴的任意内容；服务端会归一大小写、连字符与 FT 前缀。</summary>
public sealed record RedeemRequest(string? Code);

/// <summary>
/// 套餐变了，所以两块数据一起返回：客户端一次性刷新套餐行与
/// 额度计量，而不用再分别请求 /me 和 /usage。
/// </summary>
public sealed record RedeemResponse(AccountDto Account, UsageDto Usage);

/// <summary>
/// 给运营人员的批量创建。<paramref name="Days"/> 是这批码每个授予的天数；
/// <paramref name="Count"/> 在处理函数里设了上限，避免一次请求铸造数千个码。
/// </summary>
public sealed record CreateCodesRequest(string? Plan, int? Days, int? Count, string? Note);

public sealed record CreateCodesResponse(IReadOnlyList<string> Codes, string Plan, int Days);

/// <summary>单个码的管理端视图。<see cref="RedeemedEmail"/> 在有人兑换前一直为 null。</summary>
public sealed record RedeemCodeDto(
    string Code,
    string Plan,
    int Days,
    string Note,
    long CreatedAt,
    string? RedeemedEmail,
    long? RedeemedAt,
    long? RevokedAt);

/// <summary>运营人员弄丢的已铸造批次可以通过这个分页接口找回。</summary>
public sealed record RedeemCodePageResponse(IReadOnlyList<RedeemCodeDto> Items, int Total, int Limit, int Offset);

// ---- 单词本（阶段一） ----

public sealed record SaveNoteRequest(
    string? Kind,
    string? SourceText,
    string? TargetText,
    string? SourceLang,
    string? TargetLang,
    string? Model,
    string? OriginApp,
    JsonElement? Dictionary = null);

public sealed record UpdateNoteRequest(string? Kind, string? Note);

public sealed record GradeNoteRequest(string? Grade);

public sealed record NoteDto(
    Guid Id,
    string Kind,
    string SourceText,
    string TargetText,
    string SourceLang,
    string TargetLang,
    string Model,
    string OriginApp,
    string Note,
    long CreatedAt,
    long UpdatedAt,
    int Stage = 0,
    long DueAt = 0,
    string LastGrade = "",
    int ReviewCount = 0,
    int Lapses = 0,
    long ReviewedAt = 0,
    JsonElement? Dictionary = null);

/// <summary>一页数据加上账号总数，客户端无需第二次请求即可显示“1 / 20”。</summary>
public sealed record NotePageResponse(IReadOnlyList<NoteDto> Items, int Total, int Limit, int Offset);

/// <summary>Due = 时间触发的，Fresh = 今日额度内的新卡（设计 §6.1）。</summary>
public sealed record ReviewQueueResponse(
    IReadOnlyList<NoteDto> Due,
    IReadOnlyList<NoteDto> Fresh,
    int DueTotal,
    int FreshQuota,
    int Limit,
    int NewLimit);
