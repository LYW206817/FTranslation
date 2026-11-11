using System.Globalization;

namespace FastTranslation.Core.Account;

/// <summary>
/// 与账号服务通信时出了什么问题。线上错误码来自服务端 {"error":"..."} 响应体；
/// 服务端未明确命名的都落到 <see cref="Server"/>。
/// </summary>
public enum AccountErrorCode
{
    None = 0,
    Network,
    InvalidResponse,
    InvalidCredentials,
    EmailTaken,
    EmailInvalid,
    Verification,
    RateLimited,
    Unauthorized,
    AccountDisabled,
    SubscriptionRequired,
    CodeInvalid,
    CodeUsed,
    CodeRevoked,
    PlanDowngrade,
    Server,
    NotSignedIn,
    PasswordMismatch,
    PasswordTooShort,
    PasswordUnchanged,
    InvalidProfile,
    DeviceLimit,
    DeviceIsCurrent,
    DeviceMissing,
    ConfirmationMismatch,
    MailUnavailable,
    ResetInvalid,
}

public sealed record AccountError(AccountErrorCode Code, string Message)
{
    /// <summary>
    /// 对外的支持邮箱。先用专用个人邮箱，域名邮箱建好之后只改这一处；
    /// 客户端按钮、站点页脚与错误文案都从这里取，避免三处各写一遍后漂移。
    /// </summary>
    public const string SupportEmail = "3349325206@qq.com";

    /// <summary>面向用户的一句话；窗口原样显示，所以读起来要像建议。</summary>
    public string Display => Code switch
    {
        AccountErrorCode.Network => "连不上账号服务器,请检查网络后重试。",
        AccountErrorCode.InvalidResponse => "账号服务器返回了看不懂的内容。",
        AccountErrorCode.InvalidCredentials => "邮箱或密码不正确。",
        AccountErrorCode.EmailTaken => "这个邮箱已经注册过了,直接登录即可。",
        AccountErrorCode.EmailInvalid => "邮箱格式不对,检查一下有没有输错。",
        AccountErrorCode.Verification => "验证码不正确或已过期,请重新获取。",
        AccountErrorCode.RateLimited => "操作太频繁了,请稍后再试。",
        AccountErrorCode.Unauthorized => "登录状态已失效,请重新登录。",
        AccountErrorCode.AccountDisabled => "这个账号已被停用,请联系我们。",
        AccountErrorCode.SubscriptionRequired => "这个功能需要付费套餐,升级后即可保存。",
        AccountErrorCode.CodeInvalid => "激活码不对,检查一下有没有输错。",
        AccountErrorCode.CodeUsed => "这个激活码已经被用过了。",
        AccountErrorCode.CodeRevoked => "这张激活码已经作废了,请联系我们。",
        AccountErrorCode.PlanDowngrade => "你现在的套餐更贵,这张码用不上,先留着吧。",
        AccountErrorCode.NotSignedIn => "还没有登录账号。",
        AccountErrorCode.PasswordMismatch => "当前密码不对。",
        AccountErrorCode.PasswordTooShort => "新密码至少 8 位。",
        AccountErrorCode.PasswordUnchanged => "新密码和当前密码一样,换一个吧。",
        AccountErrorCode.InvalidProfile => "名字最多 24 个字,不能包含换行等特殊字符。",
        AccountErrorCode.DeviceLimit => "设备数量已达上限。请先在任意一台已登录的设备上打开账号页,移除一台不用的设备;如果旧设备都不在了,请联系我们。",
        AccountErrorCode.DeviceIsCurrent => "这是当前正在使用的设备,不能移除。",
        AccountErrorCode.DeviceMissing => "这台设备已经不在列表里了,刷新后再试。",
        AccountErrorCode.ConfirmationMismatch => "邮箱输入不一致。请完整输入当前账号的邮箱。",
        AccountErrorCode.MailUnavailable => $"邮箱服务暂不可用,请联系 {SupportEmail}。",
        AccountErrorCode.ResetInvalid => "重置请求已失效,请重新获取验证码。",
        _ => "账号服务器出错了,请稍后再试。",
    };
}

public sealed record AccountProfile(
    Guid Id,
    string Email,
    string Plan,
    int QuotaMonthly,
    int? QuotaDaily,
    long? PlanExpiresAtUnix = null,
    string DisplayName = "")
{
    /// <summary>界面显示的账号名：设了显示名就用显示名，否则用邮箱。</summary>
    public string DisplayNameOrEmail => string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;

    /// <summary>圆形头像用的一个字符。地址里取不出首字符时回落到 "?"（空邮箱只会在
    /// 响应格式错误时出现）。</summary>
    public string Initial => DisplayNameOrEmail.Length == 0
        ? "?"
        : DisplayNameOrEmail[..1].ToUpperInvariant();

    public string PlanDisplay => Plan switch
    {
        "light" => "Lite",
        "standard" => "Pro",
        "max" => "Max",
        _ => "免费版",
    };

    /// <summary>免费版为 null：没有购买任何东西，也就没有日期可显示。</summary>
    public string? ExpiryLine => Plan != "free" && PlanExpiresAtUnix is { } at
        ? "有效期至 " + DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("yyyy-MM-dd")
        : null;
}

/// <summary>服务端上报的一个额度窗口。占比在这里算，好让没有每日上限的套餐
/// 也不会让进度条除零。</summary>
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

/// <summary>
/// 一台已登记的设备。指纹不下发给客户端：它只是服务端用来判断「当前设备」的凭据。
/// <paramref name="LastSeenAtUnix"/> 是服务端最后一次登记/登录时间，不是每次翻译的时间。
/// </summary>
public sealed record AccountDevice(
    Guid Id,
    string Name,
    long CreatedAtUnix,
    long LastSeenAtUnix,
    bool IsCurrent)
{
    /// <summary>当前设备固定显示「这台电脑」：用户不需要猜，也避免把机器名带进截图。</summary>
    public string DisplayName => IsCurrent
        ? "这台电脑"
        : string.IsNullOrWhiteSpace(Name) ? "未命名设备" : Name;

    public string LastSeenDisplay => DescribeLastSeen(LastSeenAtUnix, DateTimeOffset.Now);

    /// <summary>相对时间；客户端时钟比服务端慢时按「刚刚」处理，不显示负数。</summary>
    public static string DescribeLastSeen(long unixSeconds, DateTimeOffset now)
    {
        var seen = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var delta = now - seen;
        if (delta < TimeSpan.FromMinutes(1)) return "刚刚";
        if (delta < TimeSpan.FromHours(1)) return Math.Max(1, (int)delta.TotalMinutes) + " 分钟前";
        if (delta < TimeSpan.FromDays(1)) return (int)delta.TotalHours + " 小时前";
        if (delta < TimeSpan.FromDays(30)) return (int)delta.TotalDays + " 天前";
        return seen.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

/// <summary>设备列表与上限。上限跟服务端策略走，客户端不写死。</summary>
public sealed record AccountDeviceList(IReadOnlyList<AccountDevice> Devices, int MaxDevices)
{
    public string CountLine => $"已登记 {Devices.Count} / {MaxDevices} 台";
}

/// <summary>客户端保存的已登录会话：令牌及其所属的资料。</summary>
public sealed record AccountSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, AccountProfile Profile);

public sealed record AccountSignIn(AccountSession Session, AccountUsage Usage);

/// <summary>兑换激活码买到的东西：新套餐以及随之更新的用量表。</summary>
public sealed record AccountRedeem(AccountProfile Profile, AccountUsage Usage);

/// <summary>
/// 这台服务器是否真的会发验证码。关闭了验证的服务器会回答 "verification_disabled"，
/// 注册表单此时必须隐藏验证码输入框，而不是拒绝空值。
/// </summary>
public sealed record CodeRequestResult(bool VerificationRequired);

/// <summary>服务端只回一个稳定消息名（code_sent / password_reset / account_deleted）的操作。</summary>
public sealed record AccountAction(string Name);

public sealed record AccountApiResult<T>(T? Value, AccountError? Error)
{
    public bool Succeeded => Error is null && Value is not null;

    public static AccountApiResult<T> Ok(T value) => new(value, null);

    public static AccountApiResult<T> Fail(AccountErrorCode code, string? message = null) =>
        new(default, new AccountError(code, message ?? code.ToString()));
}