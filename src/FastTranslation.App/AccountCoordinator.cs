using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Lexicon;
using FastTranslation.Core.Notes;

namespace FastTranslation.App;

/// <summary>
/// 负责进程中唯一的登录会话：启动时恢复它、刷新过期的 access token，并对所有调用方
/// 隐藏「token 过期，请重试」这种情况。窗口和首页都只读 <see cref="Session"/>，
/// 会话存储不经过任何其他代码。
/// </summary>
public sealed class AccountCoordinator
{
    private readonly AccountApiClient _client;
    private readonly NotesApiClient? _notes;
    private readonly AccountSessionStore _store;
    private readonly string _deviceFingerprint;
    private readonly string _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AccountSession? _session;

    public AccountCoordinator(
        AccountApiClient client,
        ISecretStore secrets,
        string? deviceFingerprint = null,
        NotesApiClient? notes = null)
    {
        _client = client;
        _notes = notes;
        _store = new AccountSessionStore(secrets);
        _deviceFingerprint = string.IsNullOrWhiteSpace(deviceFingerprint)
            ? DeviceIdentity.LoadOrCreate(secrets)
            : deviceFingerprint;
        _deviceName = DeviceIdentity.MachineName;
    }

    public AccountSession? Session => _session;

    public bool IsSignedIn => _session is not null;

    public string BaseUrl => _client.BaseUrl;
    public string NotesBaseUrl => _notes?.BaseUrl ?? _client.BaseUrl;

    /// <summary>网关按设备记日额度，所以它必须看到和登录时相同的指纹——否则每次翻译
    /// 都会落到一个新的匿名设备行上。</summary>
    public string DeviceFingerprint => _deviceFingerprint;

    /// <summary>
    /// 提供给不属于本类的调用（翻译网关）的 access token。令牌距过期不足一分钟时先行轮换：
    /// 否则一个死在 401 上的网关请求，会让每次翻译白搭一整个往返。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_session is not { } session) return null;
        var cutOff = DateTimeOffset.UtcNow.AddMinutes(1);
        if (session.ExpiresAt > cutOff) return session.AccessToken;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var current = _session;
            if (current is null) return null;
            if (current.ExpiresAt > cutOff) return current.AccessToken;
            await RefreshAsync(current, cancellationToken).ConfigureAwait(true);
            return _session?.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>会话一变就触发，好让任何可见界面都能重绘。</summary>
    public event Action? Changed;

    /// <summary>
    /// 恢复已存储的会话。仍有效的 access token 直接用；过期的先轮换。成功（已登录或确实
    /// 未登录）返回 null，失败返回对应错误码。
    /// </summary>
    public async Task<AccountError?> TryRestoreAsync(CancellationToken cancellationToken)
    {
        var stored = _store.Load();
        if (stored is null)
        {
            SetSession(null);
            return null;
        }

        var restored = new AccountSession(
            stored.AccessToken,
            stored.RefreshToken,
            stored.ExpiresAt,
            new AccountProfile(stored.UserId, stored.Email, stored.Plan, 0, null));

        if (!stored.IsExpired(DateTimeOffset.UtcNow))
        {
            // 升级之后存储里的套餐和额度会过期；只有令牌值得缓存。
            // 用量由调用方去取，它同时也会刷新资料。
            SetSession(restored);
            var profile = await _client.GetProfileAsync(restored.AccessToken, cancellationToken).ConfigureAwait(true);
            if (profile.Succeeded)
            {
                SetSession(restored with { Profile = profile.Value! });
                return null;
            }
            if (profile.Error!.Code != AccountErrorCode.Unauthorized) return null; // 保留会话；令牌仍然有效
        }

        return await RefreshAsync(restored, cancellationToken).ConfigureAwait(true);
    }

    private async Task<AccountError?> RefreshAsync(AccountSession current, CancellationToken cancellationToken)
    {
        var result = await _client.RefreshAsync(current.RefreshToken, cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            Apply(result.Value!);
            return null;
        }
        // 只有刷新令牌被拒才值得登出；网络断了则保留会话，下次启动还能再试
        if (result.Error!.Code is AccountErrorCode.Unauthorized or AccountErrorCode.InvalidResponse)
        {
            _store.Clear();
            SetSession(null);
        }
        else
        {
            SetSession(current);
        }
        return result.Error;
    }

    public async Task<AccountApiResult<AccountSignIn>> SignInAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var result = await _client.LoginAsync(email, password, _deviceFingerprint, _deviceName, cancellationToken)
            .ConfigureAwait(true);
        if (result.Succeeded) Apply(result.Value!);
        return result;
    }

    public async Task<AccountApiResult<AccountSignIn>> RegisterAsync(
        string email, string password, string? code, CancellationToken cancellationToken)
    {
        var result = await _client.RegisterAsync(email, password, code, _deviceFingerprint, _deviceName, cancellationToken)
            .ConfigureAwait(true);
        if (result.Succeeded) Apply(result.Value!);
        return result;
    }

    /// <summary>没配 SMTP 的服务端会回「验证已禁用」，表单随后就不显示这个字段。</summary>
    public Task<AccountApiResult<CodeRequestResult>> RequestCodeAsync(string email, CancellationToken cancellationToken) =>
        _client.RequestVerificationCodeAsync(email, cancellationToken);

    public Task<AccountApiResult<AccountUsage>> ReadUsageAsync(CancellationToken cancellationToken) =>
        WithSessionAsync(token => _client.GetUsageAsync(token, cancellationToken, _deviceFingerprint), cancellationToken);

    /// <summary>设备列表。带上本机指纹，服务端才能标出「这台电脑」。</summary>
    public Task<AccountApiResult<AccountDeviceList>> ReadDevicesAsync(CancellationToken cancellationToken) =>
        WithSessionAsync(token => _client.GetDevicesAsync(token, _deviceFingerprint, cancellationToken), cancellationToken);

    /// <summary>移除另一台设备；服务端会同时吊销那台设备的 refresh token。</summary>
    public Task<AccountApiResult<AccountDevice>> RemoveDeviceAsync(
        Guid deviceId, CancellationToken cancellationToken) =>
        WithSessionAsync(
            token => _client.RemoveDeviceAsync(token, deviceId, _deviceFingerprint, cancellationToken),
            cancellationToken);

    /// <summary>
    /// 兑换会改写套餐，所以刷新后的资料要就地替换已存储的那份——否则窗口会一直显示
    /// 免费版，直到下一次登录。
    /// </summary>
    public async Task<AccountApiResult<AccountRedeem>> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        var result = await WithSessionAsync(
            token => _client.RedeemAsync(token, code, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (result.Succeeded && _session is { } session)
        {
            var updated = session with { Profile = result.Value!.Profile };
            _store.Save(updated);
            SetSession(updated);
        }

        return result;
    }

    /// <summary>
    /// 显示名存在资料里，所以保存成功也必须替换已存储的资料——侧边栏和账号卡片是从
    /// 会话取标签的，不是从名字的第二份副本。
    /// </summary>
    public async Task<AccountApiResult<AccountProfile>> UpdateDisplayNameAsync(
        string displayName, CancellationToken cancellationToken)
    {
        var result = await WithSessionAsync(
            token => _client.UpdateProfileAsync(token, displayName, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (result.Succeeded && _session is { } session)
        {
            var updated = session with { Profile = result.Value! };
            _store.Save(updated);
            SetSession(updated);
        }

        return result;
    }

    /// <summary>
    /// 改密码会让服务端吊销所有会话，因此回包会给本设备带一对新令牌：调用方必须应用它，
    /// 否则被登出的就是这台客户端自己。
    /// </summary>
    public async Task<AccountApiResult<AccountSignIn>> ChangePasswordAsync(
        string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var result = await WithSessionAsync(
            token => _client.ChangePasswordAsync(
                token, currentPassword, newPassword, _deviceFingerprint, _deviceName, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            Apply(result.Value!);
        }

        return result;
    }

    /// <summary>
    /// 保存一组配对。整个单词本的请求都走会话包装层，这样过期的 access token 会被轮换，
    /// 而不是在用户刚点过的卡片上冒出「token 过期」。
    /// </summary>
    public Task<AccountApiResult<Note>> SaveNoteAsync(
        NoteKind? kind,
        string sourceText,
        string targetText,
        string sourceLang,
        string targetLang,
        string model,
        string originApp,
        CancellationToken cancellationToken,
        DictionaryEntry? dictionary = null)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<Note>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.SaveAsync(
                token, kind, sourceText, targetText, sourceLang, targetLang, model, originApp,
                cancellationToken, dictionary),
            cancellationToken);
    }

    public Task<AccountApiResult<NotePage>> ListNotesAsync(
        NoteKind? kind, string? query, int limit, int offset, CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<NotePage>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.ListAsync(token, kind, query, limit, offset, cancellationToken),
            cancellationToken);
    }

    public Task<AccountApiResult<Note>> UpdateNoteAsync(
        Guid id, NoteKind? kind, string? note, CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<Note>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.UpdateAsync(token, id, kind, note, cancellationToken), cancellationToken);
    }

    public Task<AccountApiResult<NoteDeleted>> DeleteNoteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<NoteDeleted>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(token => _notes.DeleteAsync(token, id, cancellationToken), cancellationToken);
    }

    public Task<AccountApiResult<ReviewQueue>> ReviewQueueAsync(
        long dayStart, int newLimit, int limit, CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<ReviewQueue>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.ReviewQueueAsync(token, dayStart, newLimit, limit, cancellationToken),
            cancellationToken);
    }

    public Task<AccountApiResult<Note>> GradeNoteAsync(
        Guid id, ReviewGrade grade, CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<Note>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.GradeAsync(token, id, grade, cancellationToken), cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            await _client.LogoutAsync(session.AccessToken, cancellationToken).ConfigureAwait(true);
        }
        _store.Clear();
        SetSession(null);
    }

    /// <summary>
    /// 用当前有效的 access token 执行调用，并在刷新后恰好重试一次：access token 只有
    /// 30 分钟寿命，所以「过期」是常态，不是该抛给用户的错误。
    /// </summary>
    private async Task<AccountApiResult<T>> WithSessionAsync<T>(
        Func<string, Task<AccountApiResult<T>>> call, CancellationToken cancellationToken) where T : class
    {
        if (_session is not { } session) return AccountApiResult<T>.Fail(AccountErrorCode.NotSignedIn);

        var first = await call(session.AccessToken).ConfigureAwait(true);
        if (first.Error?.Code != AccountErrorCode.Unauthorized) return first;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var current = _session;
            if (current is null) return AccountApiResult<T>.Fail(AccountErrorCode.NotSignedIn);
            if (!ReferenceEquals(current, session))
            {
                // 另一个调用方趁这次等待期间刷新过了——直接用新令牌重试就行
                return await call(current.AccessToken).ConfigureAwait(true);
            }
            var failure = await RefreshAsync(current, cancellationToken).ConfigureAwait(true);
            if (failure is not null) return AccountApiResult<T>.Fail(failure.Code, failure.Message);
            return _session is { } renewed
                ? await call(renewed.AccessToken).ConfigureAwait(true)
                : AccountApiResult<T>.Fail(AccountErrorCode.NotSignedIn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(AccountSignIn signIn)
    {
        _store.Save(signIn.Session);
        SetSession(signIn.Session);
    }

    private void SetSession(AccountSession? session)
    {
        _session = session;
        Changed?.Invoke();
    }
}