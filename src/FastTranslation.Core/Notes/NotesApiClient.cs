using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastTranslation.Core.Account;

namespace FastTranslation.Core.Notes;

/// <summary>
/// 负责 /api/v1/notes。遵循与 <see cref="AccountApiClient"/> 相同的契约：
/// 传输或格式问题从不抛异常，失败一律以 <see cref="AccountErrorCode"/> 上报，
/// 好让界面只有一条错误路径。
/// </summary>
public sealed class NotesApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public NotesApiClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = AccountApiClient.NormalizeBaseUrl(baseUrl);
    }

    public string BaseUrl => _baseUrl;

    /// <summary>
    /// 保存一对译文。重复项（同类型 + 同原文）返回已有的那行而不是报错，
    /// 所以连点两下「保存」也不会出问题。
    /// </summary>
    public async Task<AccountApiResult<Note>> SaveAsync(
        string accessToken,
        NoteKind? kind,
        string sourceText,
        string targetText,
        string sourceLang,
        string targetLang,
        string model,
        string originApp,
        CancellationToken cancellationToken)
    {
        var body = new SaveBody(
            kind is { } k ? NoteKinds.ToWire(k) : null,
            sourceText, targetText, sourceLang, targetLang, model, originApp);
        var response = await SendAsync(HttpMethod.Post, "api/v1/notes/", accessToken, body, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<NoteResponse, Note>(response, ToNote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountApiResult<NotePage>> ListAsync(
        string accessToken,
        NoteKind? kind,
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var path = "api/v1/notes/?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (kind is { } k)
        {
            path += "&kind=" + NoteKinds.ToWire(k);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += "&q=" + Uri.EscapeDataString(query.Trim());
        }

        var response = await SendAsync(HttpMethod.Get, path, accessToken, null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PageResponse, NotePage>(
            response,
            page => new NotePage(page.Items.Select(ToNote).ToList(), page.Total),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>部分更新：传 null 的字段保持不动。</summary>
    public async Task<AccountApiResult<Note>> UpdateAsync(
        string accessToken, Guid id, NoteKind? kind, string? note, CancellationToken cancellationToken)
    {
        var body = new UpdateBody(kind is { } k ? NoteKinds.ToWire(k) : null, note);
        var response = await SendAsync(
            HttpMethod.Patch, "api/v1/notes/" + id.ToString("D"), accessToken, body, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<NoteResponse, Note>(response, ToNote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountApiResult<NoteDeleted>> DeleteAsync(
        string accessToken, Guid id, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Delete, "api/v1/notes/" + id.ToString("D"), accessToken, null, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<MessageResponse, NoteDeleted>(
            response, _ => new NoteDeleted(true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>今天的复习队列。<paramref name="dayStart"/> 是调用方的本地零点。</summary>
    public async Task<AccountApiResult<ReviewQueue>> ReviewQueueAsync(
        string accessToken, long dayStart, int newLimit, int limit, CancellationToken cancellationToken)
    {
        var path = "api/v1/notes/review?dayStart=" + dayStart.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&newLimit=" + newLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await SendAsync(HttpMethod.Get, path, accessToken, null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ReviewQueueResponseWire, ReviewQueue>(
            response,
            wire => new ReviewQueue(
                wire.Due.Select(ToNote).ToList(),
                wire.Fresh.Select(ToNote).ToList(),
                wire.DueTotal,
                wire.FreshQuota),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountApiResult<Note>> GradeAsync(
        string accessToken, Guid id, ReviewGrade grade, CancellationToken cancellationToken)
    {
        var body = new GradeBody(ReviewSchedule.ToWire(grade));
        var response = await SendAsync(
            HttpMethod.Post, "api/v1/notes/" + id.ToString("D") + "/review", accessToken, body, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<NoteResponse, Note>(response, ToNote, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, string? accessToken, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + "/" + path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return null;
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
        using var document = TryParse(payload);
        if (document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String)
        {
            switch (error.GetString())
            {
                case "subscription_required": return AccountErrorCode.SubscriptionRequired;
                case "account_disabled": return AccountErrorCode.AccountDisabled;
                case "notes_limit_reached": return AccountErrorCode.Server;
                case "note_missing":
                case "kind_invalid":
                case "grade_invalid":
                case "source_text_required":
                    return AccountErrorCode.InvalidResponse;
            }
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => AccountErrorCode.Unauthorized,
            HttpStatusCode.TooManyRequests => AccountErrorCode.RateLimited,
            HttpStatusCode.Forbidden => AccountErrorCode.AccountDisabled,
            _ => AccountErrorCode.Server,
        };
    }

    private static JsonDocument? TryParse(string payload)
    {
        if (payload.Length == 0) return null;
        try { return JsonDocument.Parse(payload); }
        catch (JsonException) { return null; }
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException;

    private static Note ToNote(NoteResponse note) => new(
        note.Id,
        NoteKinds.TryParse(note.Kind, out var kind) ? kind : NoteKind.Sentence,
        note.SourceText,
        note.TargetText,
        note.SourceLang,
        note.TargetLang,
        note.Model,
        note.OriginApp,
        note.Note,
        note.CreatedAt,
        note.UpdatedAt,
        note.Stage,
        note.DueAt,
        note.LastGrade,
        note.ReviewCount,
        note.Lapses,
        note.ReviewedAt);

    // ---- 线上数据结构 ----

    private sealed record SaveBody(
        string? Kind, string SourceText, string TargetText,
        string SourceLang, string TargetLang, string Model, string OriginApp);

    private sealed record UpdateBody(string? Kind, string? Note);

    private sealed record GradeBody(string Grade);

    private sealed class MessageResponse
    {
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    }

    private sealed class NoteResponse
    {
        [JsonPropertyName("id")] public Guid Id { get; init; }
        [JsonPropertyName("kind")] public string Kind { get; init; } = "sentence";
        [JsonPropertyName("sourceText")] public string SourceText { get; init; } = string.Empty;
        [JsonPropertyName("targetText")] public string TargetText { get; init; } = string.Empty;
        [JsonPropertyName("sourceLang")] public string SourceLang { get; init; } = "auto";
        [JsonPropertyName("targetLang")] public string TargetLang { get; init; } = "auto";
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("originApp")] public string OriginApp { get; init; } = string.Empty;
        [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;
        [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; init; }
        [JsonPropertyName("stage")] public int Stage { get; init; }
        [JsonPropertyName("dueAt")] public long DueAt { get; init; }
        [JsonPropertyName("lastGrade")] public string LastGrade { get; init; } = string.Empty;
        [JsonPropertyName("reviewCount")] public int ReviewCount { get; init; }
        [JsonPropertyName("lapses")] public int Lapses { get; init; }
        [JsonPropertyName("reviewedAt")] public long ReviewedAt { get; init; }
    }

    private sealed class PageResponse
    {
        [JsonPropertyName("items")] public List<NoteResponse> Items { get; init; } = [];
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("limit")] public int Limit { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
    }

    private sealed class ReviewQueueResponseWire
    {
        [JsonPropertyName("due")] public List<NoteResponse> Due { get; init; } = [];
        [JsonPropertyName("fresh")] public List<NoteResponse> Fresh { get; init; } = [];
        [JsonPropertyName("dueTotal")] public int DueTotal { get; init; }
        [JsonPropertyName("freshQuota")] public int FreshQuota { get; init; }
        [JsonPropertyName("limit")] public int Limit { get; init; }
        [JsonPropertyName("newLimit")] public int NewLimit { get; init; }
    }
}
