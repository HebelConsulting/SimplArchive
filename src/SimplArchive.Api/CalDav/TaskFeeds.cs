using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

/// <summary>Which of the two feeds a collection is.</summary>
internal enum TaskFeedKind
{
    /// <summary>`VTODO` — what a reminder or task app subscribes to.</summary>
    Todos,

    /// <summary>`VEVENT` on the due date — what a calendar app that hides `VTODO`s shows instead.</summary>
    Deadlines,
}

/// <summary>
/// The caller's workflow review tasks, published as two read-only CalDAV collections (#650, slice 4 of #564).
/// </summary>
/// <remarks>
/// <para>
/// The My tasks tab, on a phone. Reminder apps and DAVx⁵ both sync tasks over CalDAV as <c>VTODO</c>, so this
/// needs no new protocol — only a calendar collection whose supported component set says <c>VTODO</c> rather
/// than <c>VEVENT</c>.
/// </para>
/// <para>
/// <b>Nothing is stored.</b> These collections have no <c>Document</c> behind them and no folder anywhere in
/// the archive: the items are composed from <c>WorkflowState</c> at read time. That is the whole reason they
/// are cheap — a materialised task list would mean generated documents carrying versions, audit entries,
/// retention and storage, kept in step by a worker that can drift from the workflow it mirrors. It also means
/// they do <b>not</b> appear on the WebDAV drive, which serves the document tree (ADR 0509) and has nothing
/// here to serve.
/// </para>
/// <para>
/// <b>Read-only.</b> A <c>PUT</c> or <c>DELETE</c> is refused; completing a review still happens in the
/// workbench. A <c>VTODO</c> ticked off on a phone would otherwise be a workflow transition arriving with no
/// actor, no comment and no audit story.
/// </para>
/// <para>
/// <b>Two feeds, not one, and they differ in content as well as shape.</b> A calendar app that ignores
/// <c>VTODO</c> shows nothing at all from the first; a task app handed <c>VEVENT</c>s shows a wall of
/// appointments it cannot tick off. And <c>WorkflowState.DueAt</c> is null unless the document's mask defines a
/// review SLA — so <see cref="TaskFeedKind.Todos"/> lists every assigned review while
/// <see cref="TaskFeedKind.Deadlines"/> can only list the ones that have a date. A tenant with no SLAs
/// configured will find the second feed empty, and that is correct rather than broken.
/// </para>
/// </remarks>
internal static class TaskFeeds
{
    internal const string TodosDisplayName = "My tasks";

    internal const string DeadlinesDisplayName = "My task deadlines";

    /// <summary>
    /// The collection's id, derived from the user and the feed so it is STABLE across restarts and deployments.
    /// </summary>
    /// <remarks>
    /// A client stores the collection URL when the account is set up and never asks again, so an id that
    /// changed — a fresh <c>Guid</c> per process, say — would silently orphan every subscription. Hashed rather
    /// than composed so it cannot be read back as "user X's tasks" by anyone who sees the URL, and so it can
    /// never collide with a real document id, which is what keeps the two id spaces from being confusable.
    /// </remarks>
    internal static Guid IdFor(Guid userId, TaskFeedKind kind)
    {
        var salt = kind == TaskFeedKind.Todos ? "simplarchive:feed:tasks" : "simplarchive:feed:task-deadlines";
        var hash = SHA256.HashData([.. userId.ToByteArray(), .. Encoding.UTF8.GetBytes(salt)]);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Which feed this collection id is, or null when it is an ordinary folder.</summary>
    internal static TaskFeedKind? KindOf(Guid userId, Guid collectionId) =>
        collectionId == IdFor(userId, TaskFeedKind.Todos) ? TaskFeedKind.Todos
        : collectionId == IdFor(userId, TaskFeedKind.Deadlines) ? TaskFeedKind.Deadlines
        : null;

    /// <summary>Both feeds, as the home set lists them. CalDAV only — a task is not a contact.</summary>
    internal static IReadOnlyList<DavCollection> CollectionsFor(Guid userId) =>
    [
        new(IdFor(userId, TaskFeedKind.Todos), TodosDisplayName, Writable: false, Color: null, ComponentSet: "VTODO"),
        new(IdFor(userId, TaskFeedKind.Deadlines), DeadlinesDisplayName, Writable: false, Color: null, ComponentSet: "VEVENT"),
    ];

    /// <summary>
    /// What a feed grants: see and read, nothing else. Synthesised rather than resolved, because the ACL
    /// calculator walks a document's ancestors and there is no document here — asking it would throw.
    /// </summary>
    internal static EffectiveRights Rights { get; } = new(
        CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
        CanDelete: false, CanCreateSubItems: false, CanManagePermissions: false, CanMove: false, CanAnnotate: false);

    /// <summary>
    /// A number that changes whenever the caller's tasks do — the CTag a polling client compares, and the
    /// sync-token it resumes from.
    /// </summary>
    /// <remarks>
    /// Derived from the task rows rather than from a change log, because nothing writes a change log entry for
    /// a workflow transition. The count is part of it on purpose: a task DISAPPEARING (approved, so no longer
    /// in review) moves no timestamp, and a token built only from the newest one would tell a subscriber
    /// nothing had changed while an item vanished underneath it.
    /// </remarks>
    internal static async Task<long> ChangeSequenceAsync(
        SimplArchiveDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await AssignedQuery(db, userId).ToListAsync(cancellationToken);
        return rows.Count == 0 ? 0 : rows.Max(r => r.UpdatedAt).UtcTicks + rows.Count;
    }

    /// <summary>Every item in the feed, composed from the caller's assigned reviews.</summary>
    internal static async Task<List<DavItem>> ItemsAsync(
        SimplArchiveDbContext db, Guid userId, Guid collectionId, TaskFeedKind kind, CancellationToken cancellationToken)
    {
        var rows = await AssignedQuery(db, userId).ToListAsync(cancellationToken);

        return [.. rows.Where(r => kind == TaskFeedKind.Todos || r.DueAt is not null)
            .Select(r => ToItem(collectionId, kind, r))];
    }

    /// <summary>One item by resource name, without composing the rest — the per-item path a sync client hits.</summary>
    internal static async Task<DavItem?> ItemAsync(
        SimplArchiveDbContext db, Guid userId, Guid collectionId, TaskFeedKind kind, string resourceName,
        CancellationToken cancellationToken)
    {
        if (!resourceName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(resourceName[..^4], out var stateId))
        {
            return null;
        }

        var row = await AssignedQuery(db, userId, stateId).FirstOrDefaultAsync(cancellationToken);

        // A dated-only feed must not serve an undated task, or a client that found it through one collection
        // would resolve it through the other and see a member the listing never offered.
        return row is null || (kind == TaskFeedKind.Deadlines && row.DueAt is null)
            ? null
            : ToItem(collectionId, kind, row);
    }

    /// <summary>
    /// The reviews assigned to this user — the same definition the My tasks tab uses, deliberately: two answers
    /// to "what are my tasks" that could disagree is exactly the drift this feed exists to avoid.
    /// </summary>
    /// <remarks>
    /// Every filter and the ordering are applied to the ENTITIES, before the projection — the standing EF
    /// translation rule (CLAUDE.md). Filtering or ordering the positional record afterwards does not fail at
    /// compile time or at startup; it throws when the endpoint is called, so the feed 500s while everything
    /// around it looks correct. Which is exactly what the first run of this did.
    /// </remarks>
    private static IQueryable<TaskRow> AssignedQuery(SimplArchiveDbContext db, Guid userId, Guid? stateId = null) =>
        from state in db.WorkflowStates
        where state.Status == WorkflowStatus.InReview
            && state.AssignedToUserId == userId
            && (stateId == null || state.Id == stateId)
        join version in db.DocumentVersions on state.DocumentVersionId equals version.Id
        join document in db.Documents on version.DocumentId equals document.Id // soft-delete filtered
        orderby state.UpdatedAt, state.Id
        select new TaskRow(state.Id, document.Name, state.DueAt, state.UpdatedAt);

    private sealed record TaskRow(Guid StateId, string DocumentName, DateTimeOffset? DueAt, DateTimeOffset UpdatedAt);

    private static DavItem ToItem(Guid collectionId, TaskFeedKind kind, TaskRow row) =>
        new(
            DocumentId: row.StateId,
            FolderId: collectionId,
            ResourceName: $"{row.StateId}.ics",
            ObjectKey: string.Empty,
            // The task's own last change, so the ETag moves exactly when what we serve does — a due date being
            // set, or the review being reassigned.
            ETag: $"{row.UpdatedAt.UtcTicks:x}",
            LastModified: row.UpdatedAt,
            SizeBytes: null,
            GeneratedText: Compose(kind, row));

    /// <summary>The item's iCalendar text.</summary>
    /// <remarks>
    /// The UID is derived from the workflow state's id and is the same in BOTH feeds on purpose: a person
    /// subscribed to both should see one thing twice, which their client can then de-duplicate, rather than two
    /// unrelated items that both claim to be the same review.
    /// </remarks>
    private static string Compose(TaskFeedKind kind, TaskRow row)
    {
        var uid = $"simplarchive-task-{row.StateId}";
        var stamp = Utc(row.UpdatedAt);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//SimplArchive//tasks//EN",
        };

        if (kind == TaskFeedKind.Todos)
        {
            lines.Add("BEGIN:VTODO");
            lines.Add($"UID:{uid}");
            lines.Add($"DTSTAMP:{stamp}");
            lines.Add($"SUMMARY:{Escape($"Review: {row.DocumentName}")}");

            // A task with no due date is still a task — DUE is simply absent, which is what lets this feed
            // carry every assigned review rather than only the ones whose mask defines an SLA.
            if (row.DueAt is { } due)
            {
                lines.Add($"DUE:{Utc(due)}");
            }

            lines.Add("STATUS:NEEDS-ACTION");
            lines.Add("END:VTODO");
        }
        else
        {
            // Zero-length: a deadline is an instant, not an hour of the reader's day. A client renders it as a
            // point rather than blocking out time that was never claimed.
            var due = Utc(row.DueAt!.Value);
            lines.Add("BEGIN:VEVENT");
            lines.Add($"UID:{uid}");
            lines.Add($"DTSTAMP:{stamp}");
            lines.Add($"DTSTART:{due}");
            lines.Add($"DTEND:{due}");
            lines.Add($"SUMMARY:{Escape($"Due: {row.DocumentName}")}");
            lines.Add("END:VEVENT");
        }

        lines.Add("END:VCALENDAR");
        return string.Concat(lines.Select(l => Fold(l) + "\r\n"));
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapes an iCalendar TEXT value (RFC 5545 §3.3.11).
    /// </summary>
    /// <remarks>
    /// Not optional politeness: a document called <c>Invoice 2026-003, final</c> would otherwise emit a comma
    /// that the format reads as a value separator, and the client would show a truncated summary — or reject
    /// the item. Backslash first, or it would escape the escapes.
    /// </remarks>
    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Folds a content line to 75 octets (RFC 5545 §3.1), continuing with a leading space.
    /// </summary>
    /// <remarks>
    /// Measured in BYTES, not characters: the limit is on octets, and a document name with an umlaut or an
    /// em-dash spends two or three bytes per character — so a character-counted fold produces lines that are
    /// still too long for a strict parser, in exactly the cases nobody tests with.
    /// </remarks>
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75)
        {
            return line;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;

            // 74 leaves room for the leading space the continuation adds, so a folded line is never 76 octets.
            if (bytes + size > 74)
            {
                builder.Append("\r\n ");
                bytes = 1;
            }

            builder.Append(rune);
            bytes += size;
        }

        return builder.ToString();
    }
}
