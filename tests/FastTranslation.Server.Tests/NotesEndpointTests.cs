using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

public class NotesEndpointTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public NotesEndpointTests(ServerFactory factory)
    {
        _factory = factory;
    }

    private Task<HttpClient> SignedInClientAsync(PlanTier plan = PlanTier.Standard) =>
        _factory.SignedInNotesClientAsync(plan);

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, SaveNoteRequest request) =>
        NotesTestHelpers.SaveNoteAsync(client, request);

    private static SaveNoteRequest Pair(string source, string target, string? kind = null) =>
        NotesTestHelpers.Pair(source, target, kind);

    [Fact]
    public async Task Notes_RequireAuthentication()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/v1/notes/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Save_ClassifiesAndRoundTrips()
    {
        var client = await SignedInClientAsync();

        var response = await SaveAsync(client, Pair("serendipity", "意外发现"));
        response.EnsureSuccessStatusCode();
        var note = (await response.Content.ReadFromJsonAsync<NoteDto>())!;

        Assert.Equal("word", note.Kind);
        Assert.Equal("serendipity", note.SourceText);
        Assert.Equal("意外发现", note.TargetText);
        Assert.Equal("chrome", note.OriginApp);
        Assert.True(note.Id != Guid.Empty);
    }

    [Theory]
    [InlineData("hello", "word")]
    [InlineData("give up", "phrase")]
    [InlineData("look forward to", "phrase")]
    [InlineData("I saw this video and did not laugh.", "sentence")]
    [InlineData("this is a fairly long sentence without punctuation at all", "sentence")]
    public async Task Save_UsesClassifierWhenKindMissing(string source, string expected)
    {
        var client = await SignedInClientAsync();

        var response = await SaveAsync(client, Pair(source, "译文"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<NoteDto>())!.Kind);
    }

    [Fact]
    public async Task Save_IsIdempotentForSamePair()
    {
        var client = await SignedInClientAsync();

        var first = await (await SaveAsync(client, Pair("resilient", "有韧性"))).Content.ReadFromJsonAsync<NoteDto>();
        var second = await (await SaveAsync(client, Pair("resilient", "有韧性"))).Content.ReadFromJsonAsync<NoteDto>();

        Assert.Equal(first!.Id, second!.Id);

        var page = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/");
        Assert.Equal(1, page!.Total);
    }

    [Fact]
    public async Task Save_RejectsEmptySource()
    {
        var client = await SignedInClientAsync();

        var response = await SaveAsync(client, Pair("   ", "译文"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("source_text_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task List_FiltersByKindAndSearch()
    {
        var client = await SignedInClientAsync();
        await SaveAsync(client, Pair("meticulous", "一丝不苟"));
        await SaveAsync(client, Pair("take off", "起飞"));
        await SaveAsync(client, Pair("She left the room quietly.", "她安静地离开了房间。"));

        var words = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/?kind=word");
        Assert.Single(words!.Items);
        Assert.Equal("meticulous", words.Items[0].SourceText);

        var phrases = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/?kind=phrase");
        Assert.Single(phrases!.Items);

        var all = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/");
        Assert.Equal(3, all!.Total);

        var found = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/?q=quiet");
        Assert.Single(found!.Items);
    }

    [Fact]
    public async Task List_MatchesTranslationSideToo()
    {
        var client = await SignedInClientAsync();
        await SaveAsync(client, Pair("diligent", "勤奋"));
        await SaveAsync(client, Pair("lazy", "懒惰"));

        var found = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/?q=%E5%8B%A4%E5%A5%8B");

        Assert.Single(found!.Items);
        Assert.Equal("diligent", found.Items[0].SourceText);
    }

    [Fact]
    public async Task List_RejectsUnknownKind()
    {
        var client = await SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/notes/?kind=paragraph");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_RefilesAndAnnotates()
    {
        var client = await SignedInClientAsync();
        var note = (await (await SaveAsync(client, Pair("in the long run", "从长远来看"))).Content
            .ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal("phrase", note.Kind);

        var response = await client.PatchAsJsonAsync($"/api/v1/notes/{note.Id}",
            new UpdateNoteRequest("sentence", "习语"));
        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<NoteDto>())!;

        Assert.Equal("sentence", updated.Kind);
        Assert.Equal("习语", updated.Note);
        Assert.Equal(note.SourceText, updated.SourceText);
    }

    [Fact]
    public async Task Patch_UnknownId_Is404()
    {
        var client = await SignedInClientAsync();

        var response = await client.PatchAsJsonAsync($"/api/v1/notes/{Guid.NewGuid()}",
            new UpdateNoteRequest("word", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesFromListButKeepsRowForSync()
    {
        var client = await SignedInClientAsync();
        var note = (await (await SaveAsync(client, Pair("obsolete", "过时的"))).Content
            .ReadFromJsonAsync<NoteDto>())!;

        var response = await client.DeleteAsync($"/api/v1/notes/{note.Id}");
        response.EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/");
        Assert.Equal(0, page!.Total);

        // Deleting again is a no-op miss, not a resurrection.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Notes_AreScopedToTheirOwner()
    {
        var owner = await SignedInClientAsync();
        var note = (await (await SaveAsync(owner, Pair("private", "私密"))).Content
            .ReadFromJsonAsync<NoteDto>())!;

        var intruder = await SignedInClientAsync();
        var page = await intruder.GetFromJsonAsync<NotePageResponse>("/api/v1/notes/");
        Assert.Equal(0, page!.Total);

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.DeleteAsync($"/api/v1/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.PatchAsJsonAsync($"/api/v1/notes/{note.Id}", new UpdateNoteRequest("word", null))).StatusCode);
    }

    [Fact]
    public async Task Save_TruncatesOverlongPayload()
    {
        var client = await SignedInClientAsync();
        var huge = new string('a', 6000);

        var response = await SaveAsync(client, Pair(huge, "译文"));
        response.EnsureSuccessStatusCode();

        var note = (await response.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal(5000, note.SourceText.Length);
    }
}
