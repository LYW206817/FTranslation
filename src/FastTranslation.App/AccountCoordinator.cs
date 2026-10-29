using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;

namespace FastTranslation.App;

/// <summary>
/// Owns the one signed-in session for the process: restores it at startup, refreshes an expired
/// access token, and hides the "token expired, try again" case from every caller. The window and
/// the main page both read <see cref="Session"/>; nothing else touches the session store.
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

    /// <summary>The gateway bills the per-device daily quota, so it must see the same fingerprint
    /// sign-in used — otherwise every translation lands on a fresh anonymous device row.</summary>
    public string DeviceFingerprint => _deviceFingerprint;

    /// <summary>
    /// Access token for a call this class does not own (the translation gateway). Rotates first
    /// when the token is within a minute of expiring: a gateway request that dies on 401 costs a
    /// whole wasted round trip in front of every translation.
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

    /// <summary>Raised whenever the session changes so any visible surface can re-render.</summary>
    public event Action? Changed;

    /// <summary>
    /// Restores the stored session. A live access token is used as-is; an expired one is rotated.
    /// Returns null on success (signed in or genuinely signed out) and the failure otherwise.
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
            // the plan and quotas in the stored blob go stale on an upgrade; only the token is
            // worth caching. Usage is fetched by the caller, which also refreshes the profile.
            SetSession(restored);
            var profile = await _client.GetProfileAsync(restored.AccessToken, cancellationToken).ConfigureAwait(true);
            if (profile.Succeeded)
            {
                SetSession(restored with { Profile = profile.Value! });
                return null;
            }
            if (profile.Error!.Code != AccountErrorCode.Unauthorized) return null; // keep the session; the token is still valid
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
        // a rejected refresh token is the only failure worth signing out for; a dead network keeps
        // the session so the next launch can try again
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

    /// <summary>A server with no SMTP answers "verification disabled" — the form then skips the field.</summary>
    public Task<AccountApiResult<CodeRequestResult>> RequestCodeAsync(string email, CancellationToken cancellationToken) =>
        _client.RequestVerificationCodeAsync(email, cancellationToken);

    public Task<AccountApiResult<AccountUsage>> ReadUsageAsync(CancellationToken cancellationToken) =>
        WithSessionAsync(token => _client.GetUsageAsync(token, cancellationToken, _deviceFingerprint), cancellationToken);

    /// <summary>
    /// Redeeming rewrites the plan, so the refreshed profile replaces the stored one in place —
    /// otherwise the window would keep showing 免费版 until the next sign-in.
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
    /// Saves one pair. The whole notebook rides on the session wrapper so an expired access token
    /// is rotated instead of surfacing as "token expired" in a card the user just clicked.
    /// </summary>
    public Task<AccountApiResult<Note>> SaveNoteAsync(
        NoteKind? kind,
        string sourceText,
        string targetText,
        string sourceLang,
        string targetLang,
        string model,
        string originApp,
        CancellationToken cancellationToken)
    {
        if (_notes is null) return Task.FromResult(AccountApiResult<Note>.Fail(AccountErrorCode.NotSignedIn));
        return WithSessionAsync(
            token => _notes.SaveAsync(
                token, kind, sourceText, targetText, sourceLang, targetLang, model, originApp, cancellationToken),
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
    /// Runs a call with the live access token and retries exactly once after a refresh: an
    /// access token lives 30 minutes, so "expired" is the normal case, not an error to surface.
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
                // another caller refreshed while this one waited — just retry with the new token
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