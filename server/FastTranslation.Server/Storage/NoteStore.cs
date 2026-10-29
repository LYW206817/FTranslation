using System.Globalization;
using FastTranslation.Server.Domain;
using Microsoft.Data.Sqlite;

namespace FastTranslation.Server.Storage;

public sealed record NoteRecord(
    Guid Id,
    Guid UserId,
    NoteKind Kind,
    string SourceText,
    string TargetText,
    string SourceLang,
    string TargetLang,
    string Model,
    string OriginApp,
    string Note,
    long CreatedAt,
    long UpdatedAt,
    int Stage = 0,
    long DueAt = 0,
    string LastGrade = "",
    int ReviewCount = 0,
    int Lapses = 0,
    long ReviewedAt = 0);

/// <summary>
/// Every SQL statement behind /api/v1/notes. Deletion is soft (<c>deleted_at</c>) because the same
/// rows will be synced between devices — a hard delete cannot be replayed to a client that was
/// offline when it happened.
/// </summary>
public sealed class NoteStore(ServerDatabase database)
{
    /// <summary>Hard ceiling per account so one runaway client cannot fill the disk.</summary>
    public const int MaxNotesPerAccount = 20_000;

    public NoteRecord Create(
        Guid userId, NoteKind kind, string sourceText, string targetText,
        string sourceLang, string targetLang, string model, string originApp)
    {
        var now = database.NowUnix;
        var record = new NoteRecord(
            Guid.NewGuid(), userId, kind,
            Trim(sourceText, 5000), Trim(targetText, 5000),
            Trim(sourceLang, 16), Trim(targetLang, 16), Trim(model, 64), Trim(originApp, 128), string.Empty,
            now, now);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, user_id, kind, source_text, target_text, source_lang, target_lang,
                               model, origin_app, note, created_at, updated_at, deleted_at,
                               stage, due_at, last_grade, review_count, lapses, reviewed_at)
            VALUES ($id, $userId, $kind, $source, $target, $sourceLang, $targetLang,
                    $model, $originApp, '', $now, $now, NULL,
                    0, 0, '', 0, 0, 0)
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$kind", NoteKinds.ToWire(kind));
        command.Parameters.AddWithValue("$source", record.SourceText);
        command.Parameters.AddWithValue("$target", record.TargetText);
        command.Parameters.AddWithValue("$sourceLang", record.SourceLang);
        command.Parameters.AddWithValue("$targetLang", record.TargetLang);
        command.Parameters.AddWithValue("$model", record.Model);
        command.Parameters.AddWithValue("$originApp", record.OriginApp);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return record;
    }

    /// <summary>
    /// The same translation saved twice is one entry: the client's 保存 button is easy to double
    /// click, and a duplicated word in the notebook is noise, not data.
    /// </summary>
    public NoteRecord? FindLive(Guid userId, NoteKind kind, string sourceText)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, kind, source_text, target_text, source_lang, target_lang,
                   model, origin_app, note, created_at, updated_at,
                   stage, due_at, last_grade, review_count, lapses, reviewed_at
            FROM notes
            WHERE user_id = $userId AND kind = $kind AND source_text = $source AND deleted_at IS NULL
            ORDER BY created_at DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$kind", NoteKinds.ToWire(kind));
        command.Parameters.AddWithValue("$source", Trim(sourceText, 5000));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    public int CountLive(Guid userId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notes WHERE user_id = $userId AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One page of the notebook. <paramref name="kind"/> null means "every shelf";
    /// <paramref name="query"/> matches either side of the pair, case-insensitively.
    /// </summary>
    public IReadOnlyList<NoteRecord> List(Guid userId, NoteKind? kind, string? query, int limit, int offset)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        var sql = new System.Text.StringBuilder("""
            SELECT id, user_id, kind, source_text, target_text, source_lang, target_lang,
                   model, origin_app, note, created_at, updated_at,
                   stage, due_at, last_grade, review_count, lapses, reviewed_at
            FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL
            """);
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        if (kind is { } shelf)
        {
            sql.Append(" AND kind = $kind");
            command.Parameters.AddWithValue("$kind", NoteKinds.ToWire(shelf));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            sql.Append(" AND (source_text LIKE $query ESCAPE '\\' OR target_text LIKE $query ESCAPE '\\')");
            command.Parameters.AddWithValue("$query", "%" + EscapeLike(query.Trim()) + "%");
        }
        sql.Append(" ORDER BY created_at DESC LIMIT $limit OFFSET $offset");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        command.CommandText = sql.ToString();

        var items = new List<NoteRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadNote(reader));
        return items;
    }

    public NoteRecord? FindById(Guid userId, Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, kind, source_text, target_text, source_lang, target_lang,
                   model, origin_app, note, created_at, updated_at,
                   stage, due_at, last_grade, review_count, lapses, reviewed_at
            FROM notes
            WHERE id = $id AND user_id = $userId AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    /// <summary>Partial update: a null argument leaves that column alone (the client sends only what changed).</summary>
    public NoteRecord? Update(Guid userId, Guid id, NoteKind? kind, string? note)
    {
        var existing = FindById(userId, id);
        if (existing is null) return null;

        var updated = existing with
        {
            Kind = kind ?? existing.Kind,
            Note = note is null ? existing.Note : Trim(note, 2000),
            UpdatedAt = database.NowUnix,
        };

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET kind = $kind, note = $note, updated_at = $now WHERE id = $id AND user_id = $userId";
        command.Parameters.AddWithValue("$kind", NoteKinds.ToWire(updated.Kind));
        command.Parameters.AddWithValue("$note", updated.Note);
        command.Parameters.AddWithValue("$now", updated.UpdatedAt);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.ExecuteNonQuery();
        return updated;
    }

    public bool SoftDelete(Guid userId, Guid id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes SET deleted_at = $now, updated_at = $now
            WHERE id = $id AND user_id = $userId AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$now", database.NowUnix);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Cards whose due time has passed, oldest first. Stage 0 is excluded: a new card is queued by quota, not by time.</summary>
    public IReadOnlyList<NoteRecord> ListDue(Guid userId, long nowUnix, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, kind, source_text, target_text, source_lang, target_lang,
                   model, origin_app, note, created_at, updated_at,
                   stage, due_at, last_grade, review_count, lapses, reviewed_at
            FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL AND stage >= 1 AND due_at <= $now
            ORDER BY due_at ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$now", nowUnix);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var items = new List<NoteRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadNote(reader));
        return items;
    }

    /// <summary>Ungraded cards saved today, oldest first. Stage 0 is exactly "never graded", so no extra queue table is needed.</summary>
    public (IReadOnlyList<NoteRecord> Items, int Total) ListFresh(Guid userId, long dayStartUnix, int limit)
    {
        using var connection = database.Open();

        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL AND stage = 0 AND created_at >= $dayStart
            """;
        count.Parameters.AddWithValue("$userId", userId.ToString("D"));
        count.Parameters.AddWithValue("$dayStart", dayStartUnix);
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, kind, source_text, target_text, source_lang, target_lang,
                   model, origin_app, note, created_at, updated_at,
                   stage, due_at, last_grade, review_count, lapses, reviewed_at
            FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL AND stage = 0 AND created_at >= $dayStart
            ORDER BY created_at ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$dayStart", dayStartUnix);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var items = new List<NoteRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadNote(reader));
        return (items, total);
    }

    /// <summary>How many new cards today's quota has already consumed.</summary>
    public int CountFreshSince(Guid userId, long dayStartUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL AND stage = 0 AND created_at >= $dayStart
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$dayStart", dayStartUnix);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes one grade. Counters move here rather than in the endpoint so the "forgot increments
    /// lapses" rule cannot drift away from the stage it belongs to.
    /// </summary>
    public NoteRecord? SetReview(
        Guid userId, Guid id, int stage, long dueAt, string grade, long reviewedAt)
    {
        var existing = FindById(userId, id);
        if (existing is null) return null;

        var lapses = existing.Lapses + (grade == "forgot" ? 1 : 0);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
            SET stage = $stage, due_at = $dueAt, last_grade = $grade,
                review_count = review_count + 1, lapses = $lapses,
                reviewed_at = $reviewedAt, updated_at = $reviewedAt
            WHERE id = $id AND user_id = $userId
            """;
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$dueAt", dueAt);
        command.Parameters.AddWithValue("$grade", grade);
        command.Parameters.AddWithValue("$lapses", lapses);
        command.Parameters.AddWithValue("$reviewedAt", reviewedAt);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.ExecuteNonQuery();
        return FindById(userId, id);
    }

    private static string Trim(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static NoteRecord ReadNote(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        NoteKinds.TryParse(reader.GetString(2), out var kind) ? kind : NoteKind.Sentence,
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt64(10),
        reader.GetInt64(11),
        reader.GetInt32(12),
        reader.GetInt64(13),
        reader.GetString(14),
        reader.GetInt32(15),
        reader.GetInt32(16),
        reader.GetInt64(17));
}
