using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTranslation.Core.Account;

/// <summary>
/// Talks to the FastTranslation account service (/api/v1/auth, /api/v1/account).
/// Hand-written over HttpClient + System.Text.Json: the project forbids HTTP frameworks and the
/// surface is five calls. Never throws for network or format problems — every failure comes back
/// as <see cref="AccountErrorCode"/> so the UI has one path for "something went wrong".
/// </summary>
public sealed class AccountApiClient
{
    /// <summary>Development default. The production host is rewritten once the server is deployed.</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:5199";

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

    /// <summary>Rotates the refresh token; the old one is spent server-side, so always store the new pair.</summary>
    public async Task<AccountApiResult<AccountSignIn>> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        var body = new RefreshBody(refreshToken);
        return await SendSignInAsync("api/v1/auth/refresh", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the server to mail a sign-up code. "verification_disabled" means this server does not
    /// verify email at all — the caller must skip the code field rather than show a dead input.
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
    /// Saves the display name. An empty string clears it, which the UI draws as the email — that is
    /// a legitimate update, not a validation failure, so it is sent as-is.
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
    /// Replaces the password. The server answers with a fresh token pair because it revokes every
    /// refresh token: other devices must sign in again, this one keeps working.
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
    /// <paramref name="deviceFingerprint"/> is what makes the daily line meaningful: the gateway
    /// meters translation per device, so without it the server can only report the account month.
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

    /// <summary>Best effort: a failed sign-out must never block the local token wipe.</summary>
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
    /// Turns a purchased activation code into days of subscription. The reply carries the new plan
    /// and the refreshed meters, so the caller never has to re-fetch the profile behind it.
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

    /// <summary>Null means the transport itself failed; the caller reports <see cref="AccountErrorCode.Network"/>.</summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, string? accessToken, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + "/" + path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        // ResponseHeadersRead: a hung body must surface as a timeout, not as a frozen UI thread.
        // The call itself is guarded: HttpClient throws for DNS/refused/TLS/timeout, and a caller
        // must never see those as exceptions.
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return null; // DNS, refused connection, TLS, timeout — all one "network" answer
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
                case "redeem_plan_downgrade": return AccountErrorCode.PlanDowngrade;
                case "password_mismatch": return AccountErrorCode.PasswordMismatch;
                case "password_too_short": return AccountErrorCode.PasswordTooShort;
                case "password_unchanged": return AccountErrorCode.PasswordUnchanged;
                case "display_name_too_long":
                case "display_name_invalid": return AccountErrorCode.InvalidProfile;
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

    // ---- wire shapes (camelCase from the server, matched case-insensitively) ----

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