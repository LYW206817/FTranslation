namespace FastTranslation.Server.Api;

/// <summary>
/// Wire contracts. Field names are camelCase on the wire (default JSON policy) and are the
/// contract with the WPF client — renaming any of them is a breaking client change.
/// </summary>
public sealed record RegisterRequest(string Email, string Password, string? Code, string? DeviceFingerprint, string? DeviceName);

public sealed record LoginRequest(string Email, string Password, string? DeviceFingerprint, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken);

public sealed record VerificationCodeRequest(string Email);

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

/// <summary><paramref name="PlanExpiresAt"/> is unix seconds; null on a paid plan means "no end date".</summary>
public sealed record AccountDto(
    Guid Id,
    string Email,
    string Plan,
    int QuotaMonthly,
    int? QuotaDaily,
    long? PlanExpiresAt,
    string DisplayName = "");

/// <summary>An empty <c>DisplayName</c> clears it: the UI then falls back to the email.</summary>
public sealed record UpdateProfileRequest(string? DisplayName);

/// <summary>
/// Changing the password returns a fresh token pair, so the device that did it stays signed in
/// while every other refresh token is revoked.
/// </summary>
public sealed record ChangePasswordRequest(
    string? CurrentPassword, string? NewPassword, string? DeviceFingerprint, string? DeviceName);

public sealed record UsageDto(
    int UsedMonthlyChars,
    int? UsedDailyChars,
    int QuotaMonthlyChars,
    int? QuotaDailyChars,
    int RemainingMonthlyChars,
    int? RemainingDailyChars,
    string Month,
    string Day);

public sealed record ErrorResponse(string Error, string? Message = null);

public sealed record AdminMetricsResponse(
    int TotalUsers,
    int FreeUsers,
    int LightUsers,
    int StandardUsers,
    int ActiveUsersLast30Days);

public sealed record MessageResponse(string Message);

public sealed record DeviceRequest(string? Fingerprint, string? Name);

public sealed record DeviceDto(Guid Id, string Name, long CreatedAt, long LastSeenAt);

/// <summary>
/// <paramref name="Days"/> null leaves any existing end date alone; 0 clears it (permanent grant);
/// a positive number sets expiry to now + that many days.
/// </summary>
public sealed record PlanChangeRequest(string? Plan, int? Days = null);

// ---- redeem (activation codes) ----

/// <summary>Whatever the buyer pasted; the server folds case, dashes and the FT prefix.</summary>
public sealed record RedeemRequest(string? Code);

/// <summary>
/// The plan changed, so both blocks travel together: the client refreshes the plan line and the
/// quota meters in one round trip instead of re-fetching /me and /usage.
/// </summary>
public sealed record RedeemResponse(AccountDto Account, UsageDto Usage);

/// <summary>
/// Batch for the operator. <paramref name="Days"/> is the length every code in the batch grants;
/// <paramref name="Count"/> is capped in the handler so one request cannot mint thousands.
/// </summary>
public sealed record CreateCodesRequest(string? Plan, int? Days, int? Count, string? Note);

public sealed record CreateCodesResponse(IReadOnlyList<string> Codes, string Plan, int Days);

/// <summary>Admin view of one code. <see cref="RedeemedEmail"/> stays null until somebody spends it.</summary>
public sealed record RedeemCodeDto(
    string Code,
    string Plan,
    int Days,
    string Note,
    long CreatedAt,
    string? RedeemedEmail,
    long? RedeemedAt,
    long? RevokedAt);

/// <summary>The minted batch an operator lost track of is recoverable through this page.</summary>
public sealed record RedeemCodePageResponse(IReadOnlyList<RedeemCodeDto> Items, int Total, int Limit, int Offset);

// ---- notes (phase 1 notebook) ----

public sealed record SaveNoteRequest(
    string? Kind,
    string? SourceText,
    string? TargetText,
    string? SourceLang,
    string? TargetLang,
    string? Model,
    string? OriginApp);

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
    long ReviewedAt = 0);

/// <summary>One page plus the account-wide total, so the client can show "1 / 20" without a second call.</summary>
public sealed record NotePageResponse(IReadOnlyList<NoteDto> Items, int Total, int Limit, int Offset);

/// <summary>Due = time-triggered, Fresh = today's new cards under quota (design §6.1).</summary>
public sealed record ReviewQueueResponse(
    IReadOnlyList<NoteDto> Due,
    IReadOnlyList<NoteDto> Fresh,
    int DueTotal,
    int FreshQuota,
    int Limit,
    int NewLimit);
