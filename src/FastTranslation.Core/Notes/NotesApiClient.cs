using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastTranslation.Core.Account;

namespace FastTranslation.Core.Notes;

/// <summary>
/// Talks to /api/v1/notes. Follows the same contract as <see cref="AccountApiClient"/>: never
/// throws for transport or format problems, and reports failures as <see cref="AccountErrorCode"/>
/// so the UI has one error path.
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
    /// Saves a pair. A duplicate (same kind + same source) returns the existing row rather than
    /// an error, so a double-clicked 保存 is harmless.
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

    /// <summary>Partial update: null leaves that field alone.</summary>
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
        note.UpdatedAt);

    // ---- wire shapes ----

    private sealed record SaveBody(
        string? Kind, string SourceText, string TargetText,
        string SourceLang, string TargetLang, string Model, string OriginApp);

    private sealed record UpdateBody(string? Kind, string? Note);

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
    }

    private sealed class PageResponse
    {
        [JsonPropertyName("items")] public List<NoteResponse> Items { get; init; } = [];
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("limit")] public int Limit { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
    }
}
