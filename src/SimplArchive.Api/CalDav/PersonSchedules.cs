using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// The caller's own time, as a read-only CalDAV collection per resource document that represents them
/// (ADR 0775) — a pilot subscribes to their dossier and sees every flight they are on, wherever the flight
/// itself is filed.
/// </summary>
/// <remarks>
/// <para>
/// A booking is ONE document (ADR 0774), living in the Schedule of the resource that holds it — the
/// aircraft. The people on that flight hold claims, not documents, so their calendars have nothing to
/// enumerate: <c>DavTree</c> lists a collection as its child documents, and a person's claims are children
/// of nothing. This composes them instead.
/// </para>
/// <para>
/// Modelled directly on <see cref="TaskFeeds"/>, which is already a computed collection and solved every
/// piece of this: a hashed stable id, a synthesised rights answer, and write paths that refuse. The one
/// difference is what the items ARE — a task feed composes its `.ics` text, while these items are real
/// documents served from storage, so they carry the booking document's own object key and ETag.
/// </para>
/// <para>
/// <b>Read-only, and that is the design rather than a limitation.</b> A booking is not made by writing into
/// your own calendar; it is made by writing into the aircraft's Schedule, which is where the conflict check
/// and the ATTENDEE expansion live (#1091). A PUT here would be a second way to create a booking, and a rule
/// enforced at one entrance is not a rule.
/// </para>
/// </remarks>
internal static class PersonSchedules
{
    /// <summary>
    /// The collection's id, derived from the resource document so it is STABLE across restarts.
    /// </summary>
    /// <remarks>
    /// A client stores the collection URL when the account is set up and never asks again, so an id that
    /// changed would silently orphan every subscription. Hashed rather than composed, exactly as the task
    /// feeds are: it cannot be read back as "the schedule of document X" by anyone who sees the URL, and it
    /// cannot collide with a real document id, which is what keeps the two id spaces unconfusable.
    /// </remarks>
    internal static Guid IdFor(Guid resourceDocumentId)
    {
        var hash = SHA256.HashData(
            [.. resourceDocumentId.ToByteArray(), .. Encoding.UTF8.GetBytes("simplarchive:feed:person-schedule")]);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>What a feed grants: see and read, nothing else — synthesised, because the ACL calculator
    /// walks a document's ancestors and there is no document here to walk from.</summary>
    /// <remarks>
    /// Granting read at the COLLECTION says nothing about its items: each one is a real booking document and
    /// is served only because the caller is a claimant on it, which the rights calculator decides on its own
    /// (ADR 0775). The two agree by construction — the feed lists exactly the claims that grant the read.
    /// </remarks>
    internal static EffectiveRights Rights { get; } = new(
        CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
        CanDelete: false, CanCreateSubItems: false, CanManagePermissions: false, CanMove: false, CanAnnotate: false);

    /// <summary>The resource document this collection id belongs to, or null when it is not one of the
    /// caller's. A hash cannot be reversed, so the caller's own resources are hashed and compared.</summary>
    /// <remarks>
    /// Scoped to the CALLER's resources deliberately: it is what stops one person's collection id, once seen,
    /// resolving for anybody else who happens to send it.
    /// </remarks>
    internal static async Task<Guid?> ResourceForAsync(
        SimplArchiveDbContext db, Guid userId, Guid collectionId, CancellationToken cancellationToken)
    {
        foreach (var resourceId in await MineAsync(db, userId, cancellationToken))
        {
            if (IdFor(resourceId) == collectionId)
            {
                return resourceId;
            }
        }

        return null;
    }

    /// <summary>The caller's schedules, as the home set lists them — one per resource that represents them.</summary>
    internal static async Task<List<DavCollection>> CollectionsForAsync(
        SimplArchiveDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var mine = await MineAsync(db, userId, cancellationToken);
        if (mine.Count == 0)
        {
            return [];
        }

        // Named after the document, not "My schedule": a person may hold more than one resource, and two
        // collections with the same name are two a client cannot tell apart.
        var names = await db.Documents
            .Where(d => mine.Contains(d.Id))
            .Select(d => new { d.Id, d.Name })
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);

        return
        [
            .. mine.Where(names.ContainsKey)
                .Select(id => new DavCollection(IdFor(id), names[id], Writable: false, Color: null, ComponentSet: "VEVENT"))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// A number that changes whenever the caller's claims do — the CTag a polling client compares.
    /// </summary>
    /// <remarks>
    /// The count is part of it for the same reason the task feed's is: a claim being CANCELLED moves no
    /// timestamp, and a token built only from the newest row would tell a subscriber nothing had changed
    /// while a flight vanished from under them. Built from the claims rather than a change log, because
    /// nothing writes a change-log entry when a booking row changes.
    /// </remarks>
    internal static async Task<long> ChangeSequenceAsync(
        SimplArchiveDbContext db, Guid resourceDocumentId, CancellationToken cancellationToken)
    {
        var rows = await ClaimQuery(db, resourceDocumentId).ToListAsync(cancellationToken);
        return rows.Count == 0 ? 0 : rows.Max(r => r.CreatedAt).UtcTicks + rows.Count;
    }

    /// <summary>Every flight this resource is claimed for.</summary>
    internal static async Task<List<Guid>> BookingDocumentIdsAsync(
        SimplArchiveDbContext db, Guid resourceDocumentId, CancellationToken cancellationToken) =>
        [.. (await ClaimQuery(db, resourceDocumentId).ToListAsync(cancellationToken)).Select(r => r.BookingDocumentId)];

    /// <summary>The resource documents that represent this user.</summary>
    private static async Task<List<Guid>> MineAsync(
        SimplArchiveDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.ResourcePrincipals
            .Where(p => p.UserId == userId)
            .Select(p => p.ResourceDocumentId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The ACTIVE claims on one resource, oldest first.
    /// </summary>
    /// <remarks>
    /// Every filter and the ordering are applied to the ENTITY before the projection — the standing EF
    /// translation rule. Filtering or ordering the positional record afterwards compiles, starts, and then
    /// throws when the endpoint is called, which is exactly how the task feed's first version failed.
    /// </remarks>
    private static IQueryable<ClaimRow> ClaimQuery(SimplArchiveDbContext db, Guid resourceDocumentId) =>
        from claim in db.ResourceBookings
        where claim.ResourceDocumentId == resourceDocumentId && claim.Status == BookingStatus.Active
        orderby claim.StartsAtUtc, claim.Id
        select new ClaimRow(claim.BookingDocumentId, claim.CreatedAt);

    private sealed record ClaimRow(Guid BookingDocumentId, DateTimeOffset CreatedAt);
}
