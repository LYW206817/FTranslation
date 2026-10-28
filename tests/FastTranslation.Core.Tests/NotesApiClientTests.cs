using System.Net;
using System.Net.Http.Headers;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;
using Xunit;

namespace FastTranslation.Core.Tests;

public class NotesApiClientTests
{
    private const string NoteJson = """
    {
      "id": "8f1a2c3d-0000-4000-8000-0000000000aa",
      "kind": "phrase",
      "sourceText": "give up",
      "targetText": "放弃",
      "sourceLang": "en",
      "targetLang": "zh",
      "model": "qwen-mt-flash",
      "originApp": "chrome",
      "note": "习语",
      "createdAt": 1761700000,
      "updatedAt": 1761700100
    }
    """;

    private const string PageJson = """
    {
      "items": [
        { "id": "8f1a2c3d-0000-4000-8000-0000000000aa", "kind": "word", "sourceText": "hello", "targetText": "你好", "sourceLang": "en", "targetLang": "zh", "model": "qwen-mt-flash", "originApp": "", "note": "", "createdAt": 1761700000, "updatedAt": 1761700000 },
        { "id": "8f1a2c3d-0000-4000-8000-0000000000bb", "kind": "sentence", "sourceText": "She left.", "targetText": "她离开了。", "sourceLang": "en", "targetLang": "zh", "model": "", "originApp": "word", "note": "", "createdAt": 1761700001, "updatedAt": 1761700001 }
      ],
      "total": 2,
      "limit": 100,
      "offset": 0
    }
    """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string LastMethod { get; private set; } = string.Empty;
        public string LastBody { get; private set; } = string.Empty;
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (NotesApiClient Client, StubHandler Handler) BuildClient(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return (new NotesApiClient(new HttpClient(handler), "https://api.example.com/"), handler);
    }

    [Fact]
    public async Task Save_Posts_to_notes_and_maps_the_row()
    {
        var (client, handler) = BuildClient(HttpStatusCode.OK, NoteJson);

        var result = await client.SaveAsync(
            "token-1", NoteKind.Phrase, "give up", "放弃", "en", "zh", "qwen-mt-flash", "chrome",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var note = result.Value!;
        Assert.Equal(NoteKind.Phrase, note.Kind);
        Assert.Equal("give up", note.SourceText);
        Assert.Equal("放弃", note.TargetText);
        Assert.Equal("习语", note.Annotation);
        Assert.Equal("https://api.example.com/api/v1/notes/", handler.LastUri!.ToString());
        Assert.Equal("POST", handler.LastMethod);
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Contains("\"kind\":\"phrase\"", handler.LastBody);
    }

    [Fact]
    public async Task Save_omits_kind_when_unknown_so_the_server_classifies()
    {
        var (client, handler) = BuildClient(HttpStatusCode.OK, NoteJson);

        var result = await client.SaveAsync(
            "token-1", null, "give up", "放弃", "en", "zh", "", "",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("\"kind\":\"", handler.LastBody);
    }

    [Fact]
    public async Task List_builds_the_query_string_and_maps_the_page()
    {
        var (client, handler) = BuildClient(HttpStatusCode.OK, PageJson);

        var result = await client.ListAsync("t", NoteKind.Word, "hel lo", 50, 10, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(NoteKind.Word, result.Value.Items[0].Kind);
        Assert.Equal(NoteKind.Sentence, result.Value.Items[1].Kind);

        var uri = handler.LastUri!.ToString();
        Assert.Contains("kind=word", uri);
        Assert.Contains("limit=50", uri);
        Assert.Contains("offset=10", uri);
        Assert.Contains("q=hel", uri);
    }

    [Fact]
    public async Task Update_patches_kind_and_note()
    {
        var (client, handler) = BuildClient(HttpStatusCode.OK, NoteJson);

        var result = await client.UpdateAsync(
            "t", Guid.Parse("8f1a2c3d-0000-4000-8000-0000000000aa"), NoteKind.Sentence, "改", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("PATCH", handler.LastMethod);
        Assert.Contains("/api/v1/notes/8f1a2c3d-0000-4000-8000-0000000000aa", handler.LastUri!.ToString());
        Assert.Contains("\"kind\":\"sentence\"", handler.LastBody);
    }

    [Fact]
    public async Task Delete_returns_ok_on_success_and_unauthorized_is_mapped()
    {
        var (ok, _) = BuildClient(HttpStatusCode.OK, """{"message":"note_deleted"}""");
        Assert.True((await ok.DeleteAsync("t", Guid.NewGuid(), CancellationToken.None)).Succeeded);

        var (denied, _) = BuildClient(HttpStatusCode.Unauthorized, """{"error":"whatever"}""");
        var failure = await denied.DeleteAsync("t", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AccountErrorCode.Unauthorized, failure.Error!.Code);
    }

    [Fact]
    public async Task Transport_failure_becomes_network()
    {
        var handler = new ThrowingHandler();
        var client = new NotesApiClient(new HttpClient(handler), "https://api.example.com");

        var result = await client.ListAsync("t", null, null, 10, 0, CancellationToken.None);

        Assert.Equal(AccountErrorCode.Network, result.Error!.Code);
    }

    [Fact]
    public void OriginLine_joins_app_and_date()
    {
        var note = new Note(Guid.NewGuid(), NoteKind.Word, "a", "b", "en", "zh", "", "chrome", "", 1761700000, 1761700000);
        Assert.StartsWith("chrome · ", note.OriginLine);

        var anon = note with { OriginApp = "" };
        Assert.DoesNotContain("·", anon.OriginLine);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }
}
