using System.Net;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>复习队列与四个档位。队列按 <c>stage</c> 划分:0 表示“从未评过分”,
/// 按当天配额计量;其余全部按 <c>due_at</c> 计量。</summary>
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
        var first = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("alpha", "甲")))
            .Content.ReadFromJsonAsync<NoteDto>())!;
        await client.SaveNoteAsync(NotesTestHelpers.Pair("beta", "乙"));
        await client.SaveNoteAsync(NotesTestHelpers.Pair("gamma", "丙"));

        var queue = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review?newLimit=2"))!;

        // 还没评分,今天的新卡配额是满的;列表按配额截到 2 张。
        Assert.Equal(2, queue.FreshQuota);
        Assert.Equal(2, queue.Fresh.Count);
        Assert.Equal(2, queue.NewLimit);

        await client.PostAsJsonAsync($"/api/v1/notes/{first.Id}/review", new GradeNoteRequest("known"));

        var after = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review?newLimit=2"))!;
        Assert.Equal(1, after.FreshQuota);
        Assert.Single(after.Fresh);
    }

    /// <summary>v101: 没学完的新卡不能随创建日期过期。把 dayStart 推到明天,
    /// 今天留下的 stage 0 仍然要在队列里,而且配额从新的一天重新算。</summary>
    [Fact]
    public async Task Review_UngradedFreshCardsSurviveIntoTheNextDay()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var first = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("alpha", "甲")))
            .Content.ReadFromJsonAsync<NoteDto>())!;
        await client.SaveNoteAsync(NotesTestHelpers.Pair("beta", "乙"));

        await client.PostAsJsonAsync($"/api/v1/notes/{first.Id}/review", new GradeNoteRequest("known"));

        // 今天已经用掉 1 个名额。
        var today = (await client.GetFromJsonAsync<ReviewQueueResponse>(
            "/api/v1/notes/review?newLimit=1"))!;
        Assert.Equal(0, today.FreshQuota);
        Assert.Empty(today.Fresh);

        // 明天:配额重置,昨天没学的 beta 仍然在队列里。
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var next = (await client.GetFromJsonAsync<ReviewQueueResponse>(
            $"/api/v1/notes/review?newLimit=1&dayStart={tomorrow}"))!;
        Assert.Equal(1, next.FreshQuota);
        Assert.Single(next.Fresh);
        Assert.Equal("beta", next.Fresh[0].SourceText);
    }

    /// <summary>同一张卡当天复习多次只占一个「新卡」名额;否则点两次「忘记」
    /// 就会把日配额翻倍消耗。</summary>
    [Fact]
    public async Task Review_QuotaCountsFirstReviewOnly()
    {
        var client = await _factory.SignedInNotesClientAsync();
        var saved = (await (await client.SaveNoteAsync(NotesTestHelpers.Pair("resilient", "有韧性")))
            .Content.ReadFromJsonAsync<NoteDto>())!;

        await client.PostAsJsonAsync($"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("forgot"));
        await client.PostAsJsonAsync($"/api/v1/notes/{saved.Id}/review", new GradeNoteRequest("fuzzy"));

        var queue = (await client.GetFromJsonAsync<ReviewQueueResponse>("/api/v1/notes/review?newLimit=2"))!;

        // 两次评分只消耗 1 个名额:还剩 1 个,但队列里已经没有别的 stage 0。
        Assert.Equal(1, queue.FreshQuota);
        Assert.Empty(queue.Fresh);
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

        // 用 404 而不是 403:陌生人不应得知该 id 存在。
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

    /// <summary>放在 store 层,因为被测的是时间:ListDue 只返回 due_at 已过
    /// 的卡,最旧的在前,且永远不包含 stage 0 那一堆。</summary>
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

            // 三张都是新卡,所以到期队列为空,新卡堆里则是全部三张。
            Assert.Empty(store.ListDue(user.Id, time.Unix, 50));
            var fresh = store.ListFresh(user.Id, 50);
            Assert.Equal(3, fresh.Count);

            // 强制三个不同的到期时间,且都在过去。
            store.SetReview(user.Id, a.Id, 1, time.Unix - 300, "forgot", time.Unix);
            store.SetReview(user.Id, b.Id, 2, time.Unix - 100, "fuzzy", time.Unix);
            store.SetReview(user.Id, c.Id, 7, time.Unix - 200, "known", time.Unix);

            var due = store.ListDue(user.Id, time.Unix, 50);
            Assert.Equal(new[] { a.Id, c.Id, b.Id }, due.Select(note => note.Id));

            // 不再有新卡:每张卡都已离开 stage 0。
            Assert.Empty(store.ListFresh(user.Id, 50));
            Assert.Equal(3, store.CountIntroducedSince(user.Id, time.Unix - 1));
            Assert.Equal(0, store.CountIntroducedSince(user.Id, time.Unix + 1));
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

    /// <summary>v101: 没学完的新卡不能因为过了零点就从队列里消失。</summary>
    [Fact]
    public void Store_ListFreshKeepsCardsFromEarlierDays()
    {
        var time = new ManualTimeProvider(1_800_000_000L);
        var (database, store, dispose) = NewStore(time);
        try
        {
            var user = new AccountStore(database).CreateUser("carry@example.com", "hash", PlanTier.Standard);
            var note = store.Create(user.Id, NoteKind.Word, "carry", "遗留", "en", "zh", "", "");

            time.Advance(TimeSpan.FromDays(3));

            var fresh = store.ListFresh(user.Id, 50);
            Assert.Single(fresh);
            Assert.Equal(note.Id, fresh[0].Id);

            // 旧实现的谓词（created_at >= 当日零点）会把这张三天前的卡排除在外；
            // 这条断言把「修的是什么」钉住，避免以后有人把过滤条件加回去。
            using var connection = database.Open();
            using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                SELECT COUNT(*) FROM notes
                WHERE user_id = $userId AND deleted_at IS NULL AND stage = 0 AND created_at >= $dayStart
                """;
            legacy.Parameters.AddWithValue("$userId", user.Id.ToString("D"));
            legacy.Parameters.AddWithValue("$dayStart", time.Unix - time.Unix % 86_400L);
            Assert.Equal(0L, (long)legacy.ExecuteScalar()!);
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>日配额只按「第一次评分」计数:同一张卡今天复习两次仍只占一个名额。</summary>
    [Fact]
    public void Store_CountIntroducedSinceUsesTheFirstReview()
    {
        var time = new ManualTimeProvider(1_800_000_000L);
        var (database, store, dispose) = NewStore(time);
        try
        {
            var user = new AccountStore(database).CreateUser("first@example.com", "hash", PlanTier.Standard);
            var a = store.Create(user.Id, NoteKind.Word, "alpha", "甲", "en", "zh", "", "");
            var b = store.Create(user.Id, NoteKind.Word, "beta", "乙", "en", "zh", "", "");

            store.SetReview(user.Id, a.Id, 1, time.Unix + 600, "forgot", time.Unix);
            store.SetReview(user.Id, b.Id, 1, time.Unix + 600, "fuzzy", time.Unix);
            // 同一张卡 60 秒后再复习一次:first_reviewed_at 不能被覆盖。
            store.SetReview(user.Id, a.Id, 2, time.Unix + 86_400, "fuzzy", time.Unix + 60);

            Assert.Equal(2, store.CountIntroducedSince(user.Id, time.Unix - 1));
            Assert.Equal(0, store.CountIntroducedSince(user.Id, time.Unix + 30));
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>v101 迁移:旧的已评分卡把 reviewed_at 回填成 first_reviewed_at,
    /// 否则它们下一次复习会被当成「今天首学」,白吃当天配额。</summary>
    [Fact]
    public void Migration_BackfillsFirstReviewedAtForExistingGradedCards()
    {
        var time = new ManualTimeProvider(1_800_000_000L);
        var (database, store, dispose) = NewStore(time);
        try
        {
            var user = new AccountStore(database).CreateUser("legacy@example.com", "hash", PlanTier.Standard);
            var note = store.Create(user.Id, NoteKind.Word, "legacy", "遗留", "en", "zh", "", "");

            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE notes SET stage = 2, reviewed_at = 123, first_reviewed_at = 0
                    WHERE id = $id
                    """;
                command.Parameters.AddWithValue("$id", note.Id.ToString("D"));
                command.ExecuteNonQuery();
            }

            database.Initialize();

            using var verify = database.Open();
            using var select = verify.CreateCommand();
            select.CommandText = "SELECT first_reviewed_at FROM notes WHERE id = $id";
            select.Parameters.AddWithValue("$id", note.Id.ToString("D"));
            Assert.Equal(123L, (long)select.ExecuteScalar()!);
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>这个版本上线时生产库已经存在,而 CREATE TABLE IF NOT EXISTS 从不
    /// 会增加列。本测试故意构造 v48 之前的形态,并证明 Initialize() 能迁移它。</summary>
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
            Assert.Equal("INTEGER", columns["first_reviewed_at"]);

            // 旧数据行变成一张未评分的卡,而不是一张坏卡。
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

/// <summary>测试可以拨动、而无需 sleep 的时钟。</summary>
internal sealed class ManualTimeProvider(long unixSeconds) : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    public long Unix => _now.ToUnixTimeSeconds();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
