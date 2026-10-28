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
    Guid Id, string Email, string Plan, int QuotaMonthly, int? QuotaDaily, long? PlanExpiresAt);

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
    long UpdatedAt);

/// <summary>One page plus the account-wide total, so the client can show "1 / 20" without a second call.</summary>
public sealed record NotePageResponse(IReadOnlyList<NoteDto> Items, int Total, int Limit, int Offset);
