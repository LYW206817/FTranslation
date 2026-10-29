namespace FastTranslation.Core.Account;

/// <summary>
/// What went wrong talking to the account service. The wire codes come from the server's
/// {"error":"..."} body; everything the server does not name lands in <see cref="Server"/>.
/// </summary>
public enum AccountErrorCode
{
    None = 0,
    Network,
    InvalidResponse,
    InvalidCredentials,
    EmailTaken,
    Verification,
    RateLimited,
    Unauthorized,
    AccountDisabled,
    SubscriptionRequired,
    CodeInvalid,
    CodeUsed,
    PlanDowngrade,
    Server,
    NotSignedIn,
}

public sealed record AccountError(AccountErrorCode Code, string Message)
{
    /// <summary>User-facing sentence; the window shows this verbatim, so it must read as advice.</summary>
    public string Display => Code switch
    {
        AccountErrorCode.Network => "连不上账号服务器,请检查网络后重试。",
        AccountErrorCode.InvalidResponse => "账号服务器返回了看不懂的内容。",
        AccountErrorCode.InvalidCredentials => "邮箱或密码不正确。",
        AccountErrorCode.EmailTaken => "这个邮箱已经注册过了,直接登录即可。",
        AccountErrorCode.Verification => "验证码不正确或已过期,请重新获取。",
        AccountErrorCode.RateLimited => "操作太频繁了,请稍后再试。",
        AccountErrorCode.Unauthorized => "登录状态已失效,请重新登录。",
        AccountErrorCode.AccountDisabled => "这个账号已被停用,请联系我们。",
        AccountErrorCode.SubscriptionRequired => "这个功能需要付费套餐,升级后即可保存。",
        AccountErrorCode.CodeInvalid => "激活码不对,检查一下有没有输错。",
        AccountErrorCode.CodeUsed => "这个激活码已经被用过了。",
        AccountErrorCode.PlanDowngrade => "你现在的套餐更贵,这张码用不上,先留着吧。",
        AccountErrorCode.NotSignedIn => "还没有登录账号。",
        _ => "账号服务器出错了,请稍后再试。",
    };
}

public sealed record AccountProfile(
    Guid Id, string Email, string Plan, int QuotaMonthly, int? QuotaDaily, long? PlanExpiresAtUnix = null)
{
    public string PlanDisplay => Plan switch
    {
        "light" => "轻享版",
        "standard" => "标准版",
        _ => "免费版",
    };

    /// <summary>Null for the free plan: nothing was bought, so there is no date to show.</summary>
    public string? ExpiryLine => Plan != "free" && PlanExpiresAtUnix is { } at
        ? "有效期至 " + DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("yyyy-MM-dd")
        : null;
}

/// <summary>One quota window as the server reports it. Fractions are computed here so the
/// progress bars never divide by zero on a plan that carries no daily limit.</summary>
public sealed record AccountUsage(
    int UsedMonthlyChars,
    int? UsedDailyChars,
    int QuotaMonthlyChars,
    int? QuotaDailyChars,
    int RemainingMonthlyChars,
    int? RemainingDailyChars,
    string Month,
    string Day)
{
    public double MonthlyFraction => QuotaMonthlyChars <= 0
        ? 0
        : Math.Clamp((double)UsedMonthlyChars / QuotaMonthlyChars, 0, 1);

    public double DailyFraction => QuotaDailyChars is not { } quota || quota <= 0
        ? 0
        : Math.Clamp((double)(UsedDailyChars ?? 0) / quota, 0, 1);

    public bool HasDaily => QuotaDailyChars is > 0;

    public string MonthlyLine => $"{UsedMonthlyChars:N0} / {QuotaMonthlyChars:N0} 字符(本月)";

    public string DailyLine => HasDaily
        ? $"{UsedDailyChars ?? 0:N0} / {QuotaDailyChars!.Value:N0} 字符(今日)"
        : "今日不限(付费版只有月度额度)";
}

/// <summary>A signed-in session as the client keeps it: tokens plus the profile they belong to.</summary>
public sealed record AccountSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, AccountProfile Profile);

public sealed record AccountSignIn(AccountSession Session, AccountUsage Usage);

/// <summary>What a redeemed activation code bought: the new plan and the meters that go with it.</summary>
public sealed record AccountRedeem(AccountProfile Profile, AccountUsage Usage);

/// <summary>
/// Whether this server actually mails a code. A server with verification turned off answers
/// "verification_disabled", and the sign-up form must then hide the code field instead of
/// rejecting an empty one.
/// </summary>
public sealed record CodeRequestResult(bool VerificationRequired);

public sealed record AccountApiResult<T>(T? Value, AccountError? Error)
{
    public bool Succeeded => Error is null && Value is not null;

    public static AccountApiResult<T> Ok(T value) => new(value, null);

    public static AccountApiResult<T> Fail(AccountErrorCode code, string? message = null) =>
        new(default, new AccountError(code, message ?? code.ToString()));
}