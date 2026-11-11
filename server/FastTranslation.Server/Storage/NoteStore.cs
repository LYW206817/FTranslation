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
    long ReviewedAt = 0,
    string DictionaryJson = "");

/// <summary>
/// /api/v1/notes 背后的所有 SQL 语句。删除是软删除（<c>deleted_at</c>），因为这些行会
/// 在设备之间同步——硬删除无法重放给当时离线的
/// 客户端。
/// </summary>
public sealed class NoteStore(ServerDatabase database)
{
    /// <summary>每账号硬上限，避免某个失控的客户端把磁盘写满。</summary>
    public const int MaxNotesPerAccount = 20_000;

    /// <summary>所有查询共用的列清单。列顺序必须与 <see cref="ReadNote"/> 的序号一致，
    /// 抄成五份时漏改一处就会让字段静默错位。</summary>
    private const string SelectColumns =
        "id, user_id, kind, source_text, target_text, source_lang, target_lang, " +
        "model, origin_app, note, created_at, updated_at, " +
        "stage, due_at, last_grade, review_count, lapses, reviewed_at, dictionary_json";

    public NoteRecord Create(
        Guid userId, NoteKind kind, string sourceText, string targetText,
        string sourceLang, string targetLang, string model, string originApp,
        string dictionaryJson = "")
    {
        var now = database.NowUnix;
        var record = new NoteRecord(
            Guid.NewGuid(), userId, kind,
            Trim(sourceText, 5000), Trim(targetText, 5000),
            Trim(sourceLang, 16), Trim(targetLang, 16), Trim(model, 64), Trim(originApp, 128), string.Empty,
            now, now, DictionaryJson: Trim(dictionaryJson, 8192));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, user_id, kind, source_text, target_text, source_lang, target_lang,
                               model, origin_app, note, created_at, updated_at, deleted_at,
                               stage, due_at, last_grade, review_count, lapses, reviewed_at, dictionary_json)
            VALUES ($id, $userId, $kind, $source, $target, $sourceLang, $targetLang,
                    $model, $originApp, '', $now, $now, NULL,
                    0, 0, '', 0, 0, 0, $dictionaryJson)
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
        command.Parameters.AddWithValue("$dictionaryJson", record.DictionaryJson);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return record;
    }

    /// <summary>
    /// 同一条翻译保存两次只算一条记录：客户端的「保存」按钮很容易被连点，
    /// 而单词本里重复的单词是噪声，不是数据。
    /// </summary>
    public NoteRecord? FindLive(Guid userId, NoteKind kind, string sourceText)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
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

    /// <summary>
    /// 某个账号的有效行。书架与搜索过滤存在的原因，是让分页总数与
    /// 实际请求的行一致：没有它们时，单词书架会报出所有书架的总数。
    /// </summary>
    public int CountLive(Guid userId, NoteKind? kind = null, string? query = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var sql = new System.Text.StringBuilder(
            "SELECT COUNT(*) FROM notes WHERE user_id = $userId AND deleted_at IS NULL");
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
        command.CommandText = sql.ToString();
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 单词本的一页。<paramref name="kind"/> 为 null 表示“全部书架”；
    /// <paramref name="query"/> 忽略大小写匹配这一对的两侧。
    /// </summary>
    public IReadOnlyList<NoteRecord> List(Guid userId, NoteKind? kind, string? query, int limit, int offset)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        var sql = new System.Text.StringBuilder($"""
            SELECT {SelectColumns}
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
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM notes
            WHERE id = $id AND user_id = $userId AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    /// <summary>
    /// 只补词典快照，不动 updated_at。用于老词条第一次从词典卡重新保存时补齐——
    /// 用户看不出这是一次写操作，不该改变「更新时间」。
    /// </summary>
    public NoteRecord? SetDictionary(Guid userId, Guid id, string dictionaryJson)
    {
        var existing = FindById(userId, id);
        if (existing is null) return null;

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET dictionary_json = $json WHERE id = $id AND user_id = $userId";
        command.Parameters.AddWithValue("$json", Trim(dictionaryJson, 8192));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.ExecuteNonQuery();
        return existing with { DictionaryJson = Trim(dictionaryJson, 8192) };
    }

    /// <summary>部分更新：参数为 null 时该列保持不变（客户端只发送变化的部分）。</summary>
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

    /// <summary>到期时间已过的卡片，最旧的在前。排除第 0 档：新卡片按配额入队，而不是按时间。</summary>
    public IReadOnlyList<NoteRecord> ListDue(Guid userId, long nowUnix, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
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

    /// <summary>
    /// 从未评分过的卡片，最旧的在前。第 0 档恰好等于“从未评分”，因此不需要额外的队列表。
    /// <para>
    /// v101: 这里**不再按 created_at 过滤**。旧实现把「今天创建的卡」当成「今天要学的卡」，
    /// 用户某天存了卡却没学完，第二天起它们既不进 due（stage 还是 0）也不进 fresh，永久消失。
    /// 日上限交给调用方按配额截断；没被学掉的卡会留到下一次。
    /// </para>
    /// </summary>
    public IReadOnlyList<NoteRecord> ListFresh(Guid userId, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL AND stage = 0
            ORDER BY created_at ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var items = new List<NoteRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadNote(reader));
        return items;
    }

    /// <summary>
    /// 这个时间窗内**第一次**被评分的卡片数，也就是已经消耗掉的日配额。
    /// 同一张卡今天再复习几次都只算一张：first_reviewed_at 只有从 0 变一次。
    /// </summary>
    public int CountIntroducedSince(Guid userId, long dayStartUnix)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM notes
            WHERE user_id = $userId AND deleted_at IS NULL
              AND stage >= 1 AND first_reviewed_at >= $dayStart
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$dayStart", dayStartUnix);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 写入一次评分。计数器在这里更新而不是在端点里，这样“遗忘会累加
    /// lapses”这条规则就不会与它所属的档位脱节。
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
                reviewed_at = $reviewedAt, updated_at = $reviewedAt,
                first_reviewed_at = CASE WHEN first_reviewed_at = 0 THEN $reviewedAt ELSE first_reviewed_at END
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
        reader.GetInt64(17),
        reader.GetString(18));
}
