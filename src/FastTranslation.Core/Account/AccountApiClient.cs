using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTranslation.Core.Account;

/// <summary>
/// 负责 FastTranslation 账号服务（/api/v1/auth、/api/v1/account）。
/// 直接手写在 HttpClient + System.Text.Json 之上：项目禁止引入 HTTP 框架，
/// 而接口面很小。网络或格式问题从不抛异常——所有失败都以
/// <see cref="AccountErrorCode"/> 返回，好让界面只有一条「出问题了」的路径。
/// </summary>
public sealed class AccountApiClient
{
    /// <summary>本机开发时的账号服务地址；`dotnet run` 默认监听 5199。</summary>
    public const string DevelopmentBaseUrl = "http://127.0.0.1:5199";

    /// <summary>备案域名，发布包使用的生产 API 根地址；协议必须是 https。</summary>
    public const string ProductionBaseUrl = "https://ftranslation.cn";

    /// <summary>
    /// 构建期选择默认地址：Debug（本机开发与测试）连 localhost，Release（发出去的安装包）
    /// 连生产。设置页的 AccountBaseUrl 非空时由 App 优先采用，可覆盖这里。
    /// 备案与 DNS 生效前不要把 Release 包发出去——那时生产地址还解析不到。
    /// </summary>
#if DEBUG
    public const string DefaultBaseUrl = DevelopmentBaseUrl;
#else
    public const string DefaultBaseUrl = ProductionBaseUrl;
#endif

    /// <summary>网关与账号接口共用的设备指纹头。放在头里而不是查询串，避免指纹进访问日志。</summary>
    public const string DeviceHeaderName = "X-FT-Device";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AccountApiClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(baseUrl);
    }

    public string BaseUrl => _baseUrl;

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim();
        if (trimmed.Length == 0) return DefaultBaseUrl;
        return trimmed.TrimEnd('/');
    }

    public async Task<AccountApiResult<AccountSignIn>> RegisterAsync(
        string email, string password, string? verificationCode, string deviceFingerprint, string? deviceName,
        CancellationToken cancellationToken)
    {
        var body = new RegisterBody(email, password, verificationCode, deviceFingerprint, deviceName);
        return await SendSignInAsync("api/v1/auth/register", body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountApiResult<AccountSignIn>> LoginAsync(
        string email, string password, string deviceFingerprint, string? deviceName,
        CancellationToken cancellationToken)
    {
        var body = new LoginBody(email, password, deviceFingerprint, deviceName);
        return await SendSignInAsync("api/v1/auth/login", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>轮换 refresh token；旧的会在服务端作废，所以务必保存新的一对令牌。</summary>
    public async Task<AccountApiResult<AccountSignIn>> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        var body = new RefreshBody(refreshToken);
        return await SendSignInAsync("api/v1/auth/refresh", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 请求服务端发送注册验证码。"verification_disabled" 表示这台服务器根本不做邮箱验证——
    /// 调用方必须跳过验证码输入框，而不是显示一个没用的输入项。
    /// </summary>
    public async Task<AccountApiResult<CodeRequestResult>> RequestVerificationCodeAsync(
        string email, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "api/v1/auth/code", null, new CodeBody(email), cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<MessageResponse, CodeRequestResult>(
            response,
            body => new CodeRequestResult(!string.Equals(body.Message, "verification_disabled", StringComparison.Ordinal)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountApiResult<AccountProfile>> GetProfileAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "api/v1/account/me", accessToken, null, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<AccountResponse, AccountProfile>(
            response, body => body.ToProfile(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存显示名。空串表示清除，界面会改显示邮箱——那是合法的更新，不是校验失败，
    /// 所以照原样发出。
    /// </summary>
    public async Task<AccountApiResult<AccountProfile>> UpdateProfileAsync(
        string accessToken, string displayName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Patch, "api/v1/account/profile", accessToken, new ProfileBody(displayName), cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<AccountResponse, AccountProfile>(
            response, body => body.ToProfile(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 修改密码。服务端会吊销所有 refresh token，因此返回一对新令牌：
    /// 其他设备需要重新登录，当前这台继续可用。
    /// </summary>
    public async Task<AccountApiResult<AccountSignIn>> ChangePasswordAsync(
        string accessToken,
        string currentPassword,
        string newPassword,
        string? deviceFingerprint,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/account/password",
            accessToken,
            new PasswordBody(currentPassword, newPassword, deviceFingerprint, deviceName),
            cancellationToken).ConfigureAwait(false);
        return await ReadAsync<AuthResponse, AccountSignIn>(
            response,
            auth => new AccountSignIn(
                new AccountSession(auth.AccessToken, auth.RefreshToken,
                    DateTimeOffset.UtcNow.AddSeconds(auth.ExpiresInSeconds), auth.Account.ToProfile()),
                auth.Usage.ToUsage()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="deviceFingerprint"/> 让「今日」那一行有意义：网关按设备计翻译量，
    /// 不给它的话服务端只能上报账号月度数据。
    /// </summary>
    public async Task<AccountApiResult<AccountUsage>> GetUsageAsync(
        string accessToken, CancellationToken cancellationToken, string? deviceFingerprint = null)
    {
        var path = string.IsNullOrWhiteSpace(deviceFingerprint)
            ? "api/v1/account/usage"
            : "api/v1/account/usage?fingerprint=" + Uri.EscapeDataString(deviceFingerprint.Trim());
        var response = await SendAsync(HttpMethod.Get, path, accessToken, null, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<UsageResponse, AccountUsage>(response, body => body.ToUsage(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 账号页的设备列表。指纹放在请求头里，服务端用它标出「这台电脑」；
    /// 不送也不会失败，只是没有当前设备标记。
    /// </summary>
    public async Task<AccountApiResult<AccountDeviceList>> GetDevicesAsync(
        string accessToken, string? deviceFingerprint, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get, "api/v1/account/devices", accessToken, null, cancellationToken, deviceFingerprint)
            .ConfigureAwait(false);
        return await ReadAsync<DeviceListResponse, AccountDeviceList>(
            response,
            body => new AccountDeviceList(body.Devices.Select(device => device.ToDevice()).ToList(), body.MaxDevices),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 移除另一台设备。当前设备的指纹照样要带上：服务端据此拒绝「移除自己」，
    /// 避免界面之外的调用把正在用的设备删掉。
    /// </summary>
    public async Task<AccountApiResult<AccountDevice>> RemoveDeviceAsync(
        string accessToken, Guid deviceId, string? deviceFingerprint, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Delete,
            "api/v1/account/devices/" + deviceId.ToString("D"),
            accessToken,
            null,
            cancellationToken,
            deviceFingerprint).ConfigureAwait(false);
        return await ReadAsync<DeviceResponse, AccountDevice>(
            response, body => body.ToDevice(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>尽力而为：退出登录失败绝不能阻塞本地令牌清除。</summary>
    public async Task<AccountApiResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Post, "api/v1/auth/logout", accessToken, new EmptyBody(), cancellationToken)
                .ConfigureAwait(false);
            response?.Dispose();
            return response is null
                ? AccountApiResult<bool>.Fail(AccountErrorCode.Network)
                : AccountApiResult<bool>.Ok(true);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return AccountApiResult<bool>.Fail(AccountErrorCode.Network, ex.Message);
        }
    }

    /// <summary>
    /// 把购买的激活码兑换成订阅天数。响应带回新套餐与刷新后的用量表，
    /// 调用方不必再跑一趟拉资料。
    /// </summary>
    public async Task<AccountApiResult<AccountRedeem>> RedeemAsync(
        string accessToken, string code, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post, "api/v1/redeem", accessToken, new RedeemBody(code), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<RedeemResponse, AccountRedeem>(
            response,
            reply => new AccountRedeem(reply.Account.ToProfile(), reply.Usage.ToUsage()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountApiResult<AccountSignIn>> SendSignInAsync(
        string path, object body, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, path, null, body, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<AuthResponse, AccountSignIn>(
            response,
            auth => new AccountSignIn(
                new AccountSession(auth.AccessToken, auth.RefreshToken,
                    DateTimeOffset.UtcNow.AddSeconds(auth.ExpiresInSeconds), auth.Account.ToProfile()),
                auth.Usage.ToUsage()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回 null 表示传输层本身失败，由调用方上报 <see cref="AccountErrorCode.Network"/>。</summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, string? accessToken, object? body, CancellationToken cancellationToken,
        string? deviceFingerprint = null)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + "/" + path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (!string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            request.Headers.TryAddWithoutValidation(DeviceHeaderName, deviceFingerprint.Trim());
        }
        // 用 ResponseHeadersRead：卡住的响应体必须表现为超时，而不是冻住 UI 线程。
        // 调用本身有保护：DNS/连接被拒/TLS/超时会让 HttpClient 抛异常，
        // 而调用方绝不能看到这些异常。
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return null; // DNS、连接被拒、TLS、超时——统一算一种「网络」答案
        }
    }

    private static async Task<AccountApiResult<TOut>> ReadAsync<TWire, TOut>(
        HttpResponseMessage? response, Func<TWire, TOut> map, CancellationToken cancellationToken)
        where TWire : class
        where TOut : class
    {
        if (response is null) return AccountApiResult<TOut>.Fail(AccountErrorCode.Network);
        using (response)
        {
            string payload;
            try
            {
                payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransport(ex))
            {
                return AccountApiResult<TOut>.Fail(AccountErrorCode.Network, ex.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return AccountApiResult<TOut>.Fail(MapFailure(response.StatusCode, payload));
            }

            try
            {
                var wire = JsonSerializer.Deserialize<TWire>(payload, JsonOptions);
                return wire is null
                    ? AccountApiResult<TOut>.Fail(AccountErrorCode.InvalidResponse)
                    : AccountApiResult<TOut>.Ok(map(wire));
            }
            catch (JsonException)
            {
                return AccountApiResult<TOut>.Fail(AccountErrorCode.InvalidResponse);
            }
        }
    }

    private static AccountErrorCode MapFailure(HttpStatusCode status, string payload)
    {
        var code = ReadErrorCode(payload);
        if (code is not null)
        {
            switch (code)
            {
                case "email_taken": return AccountErrorCode.EmailTaken;
                case "credentials_invalid": return AccountErrorCode.InvalidCredentials;
                case "account_disabled": return AccountErrorCode.AccountDisabled;
                case "code_invalid":
                case "code_expired":
                case "code_missing":
                case "code_attempts_exceeded":
                    return AccountErrorCode.Verification;
                case "code_recently_sent": return AccountErrorCode.RateLimited;
                case "redeem_code_invalid": return AccountErrorCode.CodeInvalid;
                case "redeem_code_used": return AccountErrorCode.CodeUsed;
                case "redeem_code_revoked": return AccountErrorCode.CodeRevoked;
                case "redeem_plan_downgrade": return AccountErrorCode.PlanDowngrade;
                case "password_mismatch": return AccountErrorCode.PasswordMismatch;
                case "password_too_short": return AccountErrorCode.PasswordTooShort;
                case "password_unchanged": return AccountErrorCode.PasswordUnchanged;
                case "display_name_too_long":
                case "display_name_invalid": return AccountErrorCode.InvalidProfile;
                case "device_limit_reached": return AccountErrorCode.DeviceLimit;
                case "device_is_current": return AccountErrorCode.DeviceIsCurrent;
                case "device_missing": return AccountErrorCode.DeviceMissing;
            }
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => AccountErrorCode.Unauthorized,
            HttpStatusCode.TooManyRequests => AccountErrorCode.RateLimited,
            HttpStatusCode.Forbidden => AccountErrorCode.AccountDisabled,
            >= HttpStatusCode.InternalServerError => AccountErrorCode.Server,
            _ => AccountErrorCode.Server,
        };
    }

    private static string? ReadErrorCode(string payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException;

    // ---- 线上数据结构（服务端用 camelCase，按不区分大小写匹配） ----

    private sealed record CodeBody(string Email);

    private sealed class MessageResponse
    {
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    }

    private sealed record RegisterBody(
        string Email, string Password, string? Code, string DeviceFingerprint, string? DeviceName);

    private sealed record LoginBody(string Email, string Password, string DeviceFingerprint, string? DeviceName);

    private sealed record RefreshBody(string RefreshToken);

    private sealed record RedeemBody(string Code);

    private sealed record ProfileBody(string DisplayName);

    private sealed record PasswordBody(
        string CurrentPassword, string NewPassword, string? DeviceFingerprint, string? DeviceName);


    private sealed record EmptyBody;

    private sealed class AuthResponse
    {
        [JsonPropertyName("accessToken")] public string AccessToken { get; init; } = string.Empty;
        [JsonPropertyName("refreshToken")] public string RefreshToken { get; init; } = string.Empty;
        [JsonPropertyName("expiresInSeconds")] public int ExpiresInSeconds { get; init; }
        [JsonPropertyName("account")] public AccountResponse Account { get; init; } = new();
        [JsonPropertyName("usage")] public UsageResponse Usage { get; init; } = new();
    }

    private sealed class RedeemResponse
    {
        [JsonPropertyName("account")] public AccountResponse Account { get; init; } = new();
        [JsonPropertyName("usage")] public UsageResponse Usage { get; init; } = new();
    }

    private sealed class AccountResponse
    {
        [JsonPropertyName("id")] public Guid Id { get; init; }
        [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
        [JsonPropertyName("plan")] public string Plan { get; init; } = "free";
        [JsonPropertyName("quotaMonthly")] public int QuotaMonthly { get; init; }
        [JsonPropertyName("quotaDaily")] public int? QuotaDaily { get; init; }
        [JsonPropertyName("planExpiresAt")] public long? PlanExpiresAt { get; init; }
        [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;

        public AccountProfile ToProfile() =>
            new(Id, Email, Plan, QuotaMonthly, QuotaDaily, PlanExpiresAt, DisplayName);
    }

    private sealed class DeviceListResponse
    {
        [JsonPropertyName("devices")] public List<DeviceResponse> Devices { get; init; } = new();
        [JsonPropertyName("maxDevices")] public int MaxDevices { get; init; }
    }

    private sealed class DeviceResponse
    {
        [JsonPropertyName("id")] public Guid Id { get; init; }
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
        [JsonPropertyName("lastSeenAt")] public long LastSeenAt { get; init; }
        [JsonPropertyName("isCurrent")] public bool IsCurrent { get; init; }

        public AccountDevice ToDevice() => new(Id, Name, CreatedAt, LastSeenAt, IsCurrent);
    }

    private sealed class UsageResponse
    {
        [JsonPropertyName("usedMonthlyChars")] public int UsedMonthlyChars { get; init; }
        [JsonPropertyName("usedDailyChars")] public int? UsedDailyChars { get; init; }
        [JsonPropertyName("quotaMonthlyChars")] public int QuotaMonthlyChars { get; init; }
        [JsonPropertyName("quotaDailyChars")] public int? QuotaDailyChars { get; init; }
        [JsonPropertyName("remainingMonthlyChars")] public int RemainingMonthlyChars { get; init; }
        [JsonPropertyName("remainingDailyChars")] public int? RemainingDailyChars { get; init; }
        [JsonPropertyName("month")] public string Month { get; init; } = string.Empty;
        [JsonPropertyName("day")] public string Day { get; init; } = string.Empty;

        public AccountUsage ToUsage() => new(
            UsedMonthlyChars, UsedDailyChars, QuotaMonthlyChars, QuotaDailyChars,
            RemainingMonthlyChars, RemainingDailyChars, Month, Day);
    }
}