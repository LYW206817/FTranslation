using System.Security.Claims;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc;

namespace FastTranslation.Server.Api;

/// <summary>
/// The notebook: every saved word / phrase / sentence. The three shelves are one table filtered by
/// <c>kind</c>, so moving an item between shelves is a PATCH, not a move between tables.
/// </summary>
public static class NotesEndpoints
{
    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notes").WithTags("notes").RequireAuthorization();

        group.MapPost("/", Save);
        group.MapGet("/", List);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapGet("/review", ReviewQueue);
        group.MapPost("/{id:guid}/review", Grade);
    }

    private const int DefaultNewLimit = 20;
    private const int DefaultQueueLimit = 50;

    private static IResult Save(
        [FromBody] SaveNoteRequest request,
        ClaimsPrincipal principal,
        NoteStore store,
        AccountStore accounts,
        ServerDatabase database)
    {
        var userId = principal.GetUserId();

        // Saving is the paywall (design §5 phase 2 / D8). Checked before the idempotent lookup on
        // purpose: a free account must not even learn that a matching note already exists.
        var user = accounts.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!Entitlement.AllowsNotebook(user.Plan, user.PlanExpiresAt, database.NowUnix))
        {
            return Results.Json(new ErrorResponse("subscription_required"), statusCode: StatusCodes.Status403Forbidden);
        }

        var source = (request.SourceText ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            return Results.BadRequest(new ErrorResponse("source_text_required"));
        }

        var kind = NoteKinds.TryParse(request.Kind, out var parsed)
            ? parsed
            : NoteClassifier.Classify(source);

        // Double-clicking 保存 produces the same pair twice; that is one entry, not two.
        if (store.FindLive(userId, kind, source) is { } existing)
        {
            return Results.Ok(ToDto(existing));
        }

        if (store.CountLive(userId) >= NoteStore.MaxNotesPerAccount)
        {
            return Results.Json(new ErrorResponse("notes_limit_reached"), statusCode: StatusCodes.Status409Conflict);
        }

        var created = store.Create(
            userId,
            kind,
            source,
            (request.TargetText ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(request.SourceLang) ? "auto" : request.SourceLang!,
            string.IsNullOrWhiteSpace(request.TargetLang) ? "auto" : request.TargetLang!,
            request.Model ?? string.Empty,
            request.OriginApp ?? string.Empty);

        return Results.Ok(ToDto(created));
    }

    private static IResult List(
        [FromQuery] string? kind,
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        ClaimsPrincipal principal,
        NoteStore store)
    {
        NoteKind? shelf = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!NoteKinds.TryParse(kind, out var parsed))
            {
                return Results.BadRequest(new ErrorResponse("kind_invalid"));
            }

            shelf = parsed;
        }

        var take = Math.Clamp(limit ?? 100, 1, 500);
        var skip = Math.Max(0, offset ?? 0);
        var items = store.List(principal.GetUserId(), shelf, query, take, skip).Select(ToDto).ToList();
        return Results.Ok(new NotePageResponse(items, store.CountLive(principal.GetUserId(), shelf, query), take, skip));
    }

    private static IResult Update(
        Guid id,
        [FromBody] UpdateNoteRequest request,
        ClaimsPrincipal principal,
        NoteStore store)
    {
        NoteKind? shelf = null;
        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            if (!NoteKinds.TryParse(request.Kind, out var parsed))
            {
                return Results.BadRequest(new ErrorResponse("kind_invalid"));
            }

            shelf = parsed;
        }

        var updated = store.Update(principal.GetUserId(), id, shelf, request.Note);
        return updated is null
            ? Results.NotFound(new ErrorResponse("note_missing"))
            : Results.Ok(ToDto(updated));
    }

    private static IResult Delete(Guid id, ClaimsPrincipal principal, NoteStore store) =>
        store.SoftDelete(principal.GetUserId(), id)
            ? Results.Ok(new MessageResponse("note_deleted"))
            : Results.NotFound(new ErrorResponse("note_missing"));

    /// <summary>
    /// Today's queue. <paramref name="dayStart"/> is the caller's local midnight in unix seconds —
    /// the server never guesses a time zone (design §5.4).
    /// </summary>
    private static IResult ReviewQueue(
        [FromQuery] long? dayStart,
        [FromQuery] int? newLimit,
        [FromQuery] int? limit,
        ClaimsPrincipal principal,
        NoteStore store,
        AccountStore accounts,
        ServerDatabase database)
    {
        var userId = principal.GetUserId();
        var user = accounts.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!Entitlement.AllowsNotebook(user.Plan, user.PlanExpiresAt, database.NowUnix))
        {
            return Results.Json(new ErrorResponse("subscription_required"), statusCode: StatusCodes.Status403Forbidden);
        }

        var start = dayStart is > 0 ? dayStart.Value : StartOfTodayUtc(database.NowUnix);
        var wantedNew = Math.Clamp(newLimit ?? DefaultNewLimit, 0, 100);
        var take = Math.Clamp(limit ?? DefaultQueueLimit, 1, 200);

        var due = store.ListDue(userId, database.NowUnix, take);
        var fresh = store.ListFresh(userId, start, Math.Max(1, wantedNew));
        var alreadyQueued = store.CountFreshSince(userId, start);
        var quota = Math.Max(0, wantedNew - alreadyQueued);

        return Results.Ok(new ReviewQueueResponse(
            due.Select(ToDto).ToList(),
            fresh.Items.Select(ToDto).ToList(),
            due.Count,
            quota,
            take,
            wantedNew));
    }

    private static IResult Grade(
        Guid id,
        [FromBody] GradeNoteRequest request,
        ClaimsPrincipal principal,
        NoteStore store,
        AccountStore accounts,
        ServerDatabase database)
    {
        var userId = principal.GetUserId();
        var user = accounts.FindUserById(userId);
        if (user is null || user.Disabled)
        {
            return Results.Json(new ErrorResponse("account_disabled"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!Entitlement.AllowsNotebook(user.Plan, user.PlanExpiresAt, database.NowUnix))
        {
            return Results.Json(new ErrorResponse("subscription_required"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!ReviewSchedule.TryParseGrade(request.Grade, out var grade))
        {
            return Results.BadRequest(new ErrorResponse("grade_invalid"));
        }

        var existing = store.FindById(userId, id);
        if (existing is null)
        {
            return Results.NotFound(new ErrorResponse("note_missing"));
        }

        var now = database.NowUnix;
        var (stage, dueAt) = ReviewSchedule.Schedule(existing.Stage, grade, now);
        var updated = store.SetReview(userId, id, stage, dueAt, ReviewSchedule.ToWire(grade), now);
        return updated is null
            ? Results.NotFound(new ErrorResponse("note_missing"))
            : Results.Ok(ToDto(updated));
    }

    /// <summary>Fallback when the client omits dayStart: UTC midnight. Clients always send their own local midnight.</summary>
    private static long StartOfTodayUtc(long nowUnix) => nowUnix - (nowUnix % 86_400L);

    private static NoteDto ToDto(NoteRecord note) => new(
        note.Id,
        NoteKinds.ToWire(note.Kind),
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
}
