using System.Net;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// The review queue and the four grades. The queue is split by <c>stage</c>: 0 means "never graded"
/// and is metered by today's quota, everything else is metered by <c>due_at</c>.
/// </summary>
public class NotesReviewTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public NotesReviewTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Review_RequiresAuthentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/notes/review");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Review_FreePlanIsRefused()
    {
        var client = await _factory.SignedInNotesClientAsync(PlanTier.Free);

        var response = await client.GetAsync("/api/v1/notes/review");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("subscription_required", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Review_QueueSplitsDueAndFresh()
    {
        var client = await _factory.SignedInNotesClientAsync();
        await client.SaveNoteAsync(NotesTestHelpers.Pair("serendipity", "意外发现"));
        await client.SaveNoteAsync(NotesTestHelpers.Pair("take off", "起飞"));

        var queue = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review"))!;

        Assert.Equal(2, queue.Fresh.Count);
        Assert.Empty(queue.Due);
        Assert.Equal(0, queue.DueTotal);
        Assert.All(queue.Fresh, note => Assert.Equal(0, note.Stage));
    }

    [Fact]
    public async Task Review_FreshQuotaShrinksWhenMoreCardsThanTheLimitExist()
    {
        var client = await _factory.SignedInNotesClientAsync();
        await client.SaveNoteAsync(NotesTestHelpers.Pair("alpha", "甲"));
        await client.SaveNoteAsync(NotesTestHelpers.Pair("beta", "乙"));
        await client.SaveNoteAsync(NotesTestHelpers.Pair("gamma", "丙"));

        var queue = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review?newLimit=2"))!;

        // Every card saved today already counts against the quota, so nothing is left over.
        Assert.Equal(0, queue.FreshQuota);
        Assert.Equal(2, queue.NewLimit);
    }

    [Fact]
    public async Task Review_GradedCardLeavesTheFreshQueue()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("resilient", "有韧性")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        var graded = await client.PostAsJsonAsync($"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("known"));
        graded.EnsureSuccessStatusCode();

        var queue = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review"))!;

        Assert.Empty(queue.Fresh);
        Assert.Empty(queue.Due);
    }

    [Fact]
    public async Task Grade_InvalidGradeIs400()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("words", "词")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("easy"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("grade_invalid", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Grade_UnknownIdIs404()
    {
        var client = await _factory.SignedInNotesClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/notes/{Guid.NewGuid()}/review", new GradeNoteRequest("fuzzy"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("note_missing", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Grade_OtherUsersCardIs404()
    {
        var owner = await _factory.SignedInNotesClientAsync();
        var saved = (await (await owner.SaveNoteAsync(NotesTestHelpers.Pair("private", "私密")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        var intruder = await _factory.SignedInNotesClientAsync();
        var response = await intruder.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("known"));

        // 404 rather than 403: a stranger must not learn that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Grade_AppliesTheLadder()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("meticulous", "一丝不苟")))
            .Content.ReadFromJsonAsync<NoteDto>())!;
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("known"));
        response.EnsureSuccessStatusCode();
        var graded = (await response.Content.ReadFromJsonAsync<NoteDto>())!;

        Assert.Equal(4, graded.Stage);
        Assert.Equal("known", graded.LastGrade);
        Assert.InRange(graded.DueAt, before + 604_800L, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 604_800L);
    }

    [Fact]
    public async Task Grade_IncrementsReviewCount()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("diligent", "勤奋")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        await client.PostAsJsonAsync($"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("fuzzy"));
        var response = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("familiar"));

        var graded = (await response.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal(2, graded.ReviewCount);
    }

    [Fact]
    public async Task Grade_ForgotIncrementsLapsesOnly()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("obsolete", "过时的")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        var forgot = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("forgot"));
        var first = (await forgot.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal(1, first.Lapses);
        Assert.Equal(1, first.Stage);

        var known = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("known"));
        var second = (await known.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal(1, second.Lapses);
    }

    [Fact]
    public async Task Grade_UpdatesUpdatedAt()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("prompt", "及时的")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("known"));
        var graded = (await response.Content.ReadFromJsonAsync<NoteDto>())!;

        Assert.True(graded.UpdatedAt >= graded.CreatedAt);
        Assert.True(graded.ReviewedAt >= graded.CreatedAt);
    }

    /// <summary>
    /// Store-level because time is what is under test: ListDue only returns cards whose due_at has
    /// passed, oldest first, and never the stage-0 pile.
    /// </summary>
    [Fact]
    public void Store_ListDueOrdersByDueAndSkipsStageZero()
    {
        var time = new ManualTimeProvider(1_800_000_000L);
        var (database, store, dispose) = NewStore(time);
        try
        {
            var user = new AccountStore(database).CreateUser("due@example.com", "hash", PlanTier.Standard);
            var a = store.Create(user.Id, NoteKind.Word, "alpha", "甲", "en", "zh", "", "");
            var b = store.Create(user.Id, NoteKind.Word, "beta", "乙", "en", "zh", "", "");
            var c = store.Create(user.Id, NoteKind.Word, "gamma", "丙", "en", "zh", "", "");

            // All three are new cards, so the due queue is empty and the fresh pile holds all three.
            Assert.Empty(store.ListDue(user.Id, time.Unix, 50));
            var (fresh, freshTotal) = store.ListFresh(user.Id, 0, 50);
            Assert.Equal(3, freshTotal);
            Assert.Equal(3, fresh.Count);

            // Force three different due times, all in the past.
            store.SetReview(user.Id, a.Id, 1, time.Unix - 300, "forgot", time.Unix);
            store.SetReview(user.Id, b.Id, 2, time.Unix - 100, "fuzzy", time.Unix);
            store.SetReview(user.Id, c.Id, 7, time.Unix - 200, "known", time.Unix);

            var due = store.ListDue(user.Id, time.Unix, 50);
            Assert.Equal(new[] { a.Id, c.Id, b.Id }, due.Select(note => note.Id));

            // Nothing is fresh any more: every card has left stage 0.
            Assert.Equal(0, store.CountFreshSince(user.Id, 0));
        }
        finally
        {
            dispose();
        }
    }

    [Fact]
    public void Store_ListDueIgnoresTheFuture()
    {
        var time = new ManualTimeProvider(1_800_000_000L);
        var (database, store, dispose) = NewStore(time);
        try
        {
            var user = new AccountStore(database).CreateUser("later@example.com", "hash", PlanTier.Standard);
            var note = store.Create(user.Id, NoteKind.Word, "later", "之后", "en", "zh", "", "");
            store.SetReview(user.Id, note.Id, 1, time.Unix + 60, "forgot", time.Unix);

            Assert.Empty(store.ListDue(user.Id, time.Unix, 50));

            time.Advance(TimeSpan.FromSeconds(61));
            Assert.Single(store.ListDue(user.Id, time.Unix, 50));
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>
    /// The production database already exists when this ships, and CREATE TABLE IF NOT EXISTS never
    /// adds a column. This test builds the pre-v48 shape on purpose and proves Initialize() migrates it.
    /// </summary>
    [Fact]
    public void Migration_OldDatabaseGainsTheReviewColumns()
    {
        const string oldSchema = """
            CREATE TABLE users (
                id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL,
                plan INTEGER NOT NULL DEFAULT 0, plan_expires_at INTEGER,
                created_at INTEGER NOT NULL, disabled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE notes (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                kind TEXT NOT NULL, source_text TEXT NOT NULL, target_text TEXT NOT NULL,
                source_lang TEXT NOT NULL DEFAULT 'auto', target_lang TEXT NOT NULL DEFAULT 'auto',
                model TEXT NOT NULL DEFAULT '', origin_app TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                deleted_at INTEGER);
            INSERT INTO users (id, email, password_hash, plan, created_at, disabled)
                VALUES ('11111111-1111-1111-1111-111111111111', 'old@example.com', 'hash', 1, 1700000000, 0);
            INSERT INTO notes (id, user_id, kind, source_text, target_text, created_at, updated_at)
                VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
                        'word', 'legacy', '遗留', 1700000000, 1700000000);
            """;

        var path = Path.Combine(Path.GetTempPath(), "ft-server-tests", "migrate-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                raw.Open();
                using var create = raw.CreateCommand();
                create.CommandText = oldSchema;
                create.ExecuteNonQuery();
            }

            var database = new ServerDatabase(path);
            database.Initialize();

            using var connection = database.Open();
            using var probe = connection.CreateCommand();
            probe.CommandText = "PRAGMA table_info(notes)";
            using var reader = probe.ExecuteReader();
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                columns[reader.GetString(1)] = reader.GetString(2);
            }

            Assert.Equal("INTEGER", columns["stage"]);
            Assert.Equal("INTEGER", columns["due_at"]);
            Assert.Equal("TEXT", columns["last_grade"]);
            Assert.Equal("INTEGER", columns["review_count"]);
            Assert.Equal("INTEGER", columns["lapses"]);
            Assert.Equal("INTEGER", columns["reviewed_at"]);

            // The legacy row becomes an ungraded card instead of a broken one.
            var store = new NoteStore(database);
            var legacy = store.FindById(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"));
            Assert.NotNull(legacy);
            Assert.Equal(0, legacy!.Stage);
            Assert.Equal(0L, legacy.DueAt);
            Assert.Equal(string.Empty, legacy.LastGrade);
            Assert.Equal(0, legacy.ReviewCount);
            Assert.Equal(0, legacy.Lapses);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch (IOException) { }
            }
        }
    }

    private static (ServerDatabase Database, NoteStore Store, Action Dispose) NewStore(TimeProvider time)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "ft-server-tests", "review-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new ServerDatabase(path, time);
        database.Initialize();

        void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch (IOException) { }
            }
        }

        return (database, new NoteStore(database), Dispose);
    }
}

/// <summary>A clock the test can move without sleeping.</summary>
internal sealed class ManualTimeProvider(long unixSeconds) : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    public long Unix => _now.ToUnixTimeSeconds();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
