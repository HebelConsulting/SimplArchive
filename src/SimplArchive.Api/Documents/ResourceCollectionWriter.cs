using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Booking;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The booking primitive's WRITE side behind a resource collection's <c>.ics</c> — the claim a booking makes
/// (ADR 0744), the block that withdraws a resource (ADR 0778) and the window that offers it (ADR 0780).
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="CalendarContactClassifier"/> when the third collection took that class past the
/// 1000-line limit. The boundary is a real one rather than a size-driven cut: the classifier answers "what
/// KIND of document is this, and what are its index fields", which is equally about a contact and an
/// appointment; this answers "what does this document DO to the resource that holds it", which is the
/// booking primitive and nothing else. Rights, audit and notifications belong to the second question, and
/// they were what made the first class large.
/// </para>
/// <para>
/// It is reached only from the classifier, because ADR 0744's rule is that EVERY path writing the <c>.ics</c>
/// is a booking act: the bookings endpoint, a drop-upload and a CalDAV PUT all arrive at that one door, and
/// a second entrance here would be an entrance the invariants and the audit trail do not see.
/// </para>
/// </remarks>
internal sealed class ResourceCollectionWriter
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly INotificationService _notifications;

    internal ResourceCollectionWriter(
        SimplArchiveDbContext dbContext, IUserSystemRightsResolver userSystemRights, INotificationService notifications)
    {
        _dbContext = dbContext;
        _userSystemRights = userSystemRights;
        _notifications = notifications;
    }

    /// <summary>Creates or moves the <see cref="ResourceBooking"/> claim behind a Schedule's .ics (ADR 0744).</summary>
    /// <remarks>
    /// The row is authoritative for the slot; the indexed Start/End are its projection. A new row's booker
    /// is the version's creator — on a CalDAV PUT that is the authenticated DAV user, on the booking
    /// endpoint the caller, so "who holds the slot" is right on every path.
    /// </remarks>
    internal async Task<PendingBookingAudit> UpsertBookingRowAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.RecurrenceRule is not null)
        {
            throw new BookingRecurrenceUnsupportedException();
        }

        if (CalendarInstants.Instant(occurrence.DtStart) is not { } startsAt)
        {
            throw new BookingSlotInvalidException("The event carries no DTSTART — a booking must claim a slot.");
        }

        // An all-day event's DTEND is already exclusive (the day after); a missing DTEND collapses the
        // slot to zero extent, which the invariant refuses with the extent named.
        var endsAt = CalendarInstants.Instant(occurrence.DtEnd) ?? startsAt;

        // The room is the Schedule's parent — containment guarantees the shape (a Booking lives only
        // in a Schedule, a Schedule only in a room), so a missing grandparent is a state this code cannot
        // reach through any admitted write.
        var roomId = await _dbContext.Documents
            .Where(d => d.Id == document.ParentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (roomId is not { } resourceId)
        {
            throw new ResourceNotBookableException(
                $"The Schedule holding document {document.Id} has no parent room to claim a slot on.");
        }

        // EVERY claim of this document, not the first (ADR 0774). Picking an arbitrary one and re-pointing it
        // at the room would reassign a PERSON's claim to the aircraft — and moving one claim's slot while its
        // siblings kept the old one would fail the same-window invariant, so an edit of a multi-claim booking
        // would be refused outright. Unreachable before this slice, because nothing created a second claim.
        var claims = await _dbContext.ResourceBookings
            .Where(b => b.BookingDocumentId == document.Id)
            .ToListAsync(cancellationToken);

        var holding = claims.FirstOrDefault(c => c.ResourceDocumentId == resourceId)
            // The holding claim is identified by its RESOURCE. On a move between two Schedules the row still
            // names the old room at this point, and there is exactly one non-attendee claim, so it is that one.
            ?? claims.FirstOrDefault(c => !AttendeeResourceIds(claims, resourceId).Contains(c.ResourceDocumentId));

        // "First booking" is the DOCUMENT'S first version, not "no claim existed yet". The bookings endpoint
        // creates the claim row itself before the finalizer runs, so a claim is already there on that path and
        // every booking made in the app would otherwise be recorded as a change to something that never
        // existed. The version number is the fact that means the same thing on every entrance.
        var wasNew = version.VersionNumber is null or <= 1;
        if (holding is null)
        {
            holding = NewClaim(document, version, resourceId, startsAt, endsAt);
            _dbContext.ResourceBookings.Add(holding);
            claims.Add(holding);
        }

        holding.ResourceDocumentId = resourceId;

        // The slot moves on EVERY claim: they are one event, and the invariant requires them to agree.
        foreach (var claim in claims)
        {
            claim.StartsAtUtc = startsAt.ToUniversalTime();
            claim.EndsAtUtc = endsAt.ToUniversalTime();
        }

        await ReconcileAttendeeClaimsAsync(document, version, occurrence, claims, holding, startsAt, endsAt, cancellationToken);

        // The audit event is BUILT here and RECORDED by the caller, after the save that may still refuse this
        // booking. IAuditRecorder.RecordAsync calls SaveChangesAsync itself, so recording inline would flush
        // the staged claims early — outside the try/catch that translates a slot conflict into a 409 — and a
        // conflicting PUT would answer 500 instead. Which is exactly what it did: the re-entrancy hazard
        // written up for the CANCELLATION path, walked into on the create path.
        var resourceNames = await _dbContext.Documents
            .Where(d => claims.Select(c => c.ResourceDocumentId).Contains(d.Id))
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        return new PendingBookingAudit(
            wasNew ? Controllers.AuditActions.BookingCreated : Controllers.AuditActions.BookingChanged,
            $"{startsAt:u}–{endsAt:u} claiming {string.Join(", ", resourceNames.OrderBy(n => n, StringComparer.Ordinal))}");
    }


    /// <summary>Creates or moves the <see cref="ResourceAvailability"/> behind an Availability .ics (ADR 0780).</summary>
    /// <remarks>
    /// The simplest of the three, and deliberately so: a window claims nothing, conflicts with nothing, and
    /// suspends nothing. It records that time was offered. No rights gate either — offering your own time is
    /// not an act anyone needs authority for beyond the write permission on the collection itself, which the
    /// ACL already decides.
    ///
    /// No audit event, for the same reason: an offer that is later withdrawn leaves the row as its own
    /// history (Withdrawn rather than deleted), and a trail entry per published window would bury the acts
    /// that matter — the bookings and the groundings — under a stream of routine calendar edits.
    /// </remarks>
    internal async Task UpsertAvailabilityRowAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.RecurrenceRule is not null)
        {
            // Refused like its siblings: the row models ONE window, and a rule the server does not expand
            // would make the stored offer and the displayed one disagree. A weekly availability is a real
            // want, and it needs expansion designed rather than assumed.
            throw new BookingRecurrenceUnsupportedException();
        }

        if (CalendarInstants.Instant(occurrence.DtStart) is not { } startsAt)
        {
            throw new BookingSlotInvalidException("The event carries no DTSTART — a window must name a range.");
        }

        var endsAt = CalendarInstants.Instant(occurrence.DtEnd) ?? startsAt;

        var parentId = await _dbContext.Documents
            .Where(d => d.Id == document.ParentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (parentId is not { } resourceId)
        {
            throw new ResourceNotBookableException(
                $"The Availability collection holding document {document.Id} has no parent resource to offer.");
        }

        var window = await _dbContext.ResourceAvailability
            .FirstOrDefaultAsync(a => a.WindowDocumentId == document.Id, cancellationToken);
        if (window is null)
        {
            window = new ResourceAvailability
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                ResourceDocumentId = resourceId,
                WindowDocumentId = document.Id,
                Status = AvailabilityStatus.Offered,
                OfferedByUserId = version.CreatedByUserId,
                OfferedByServiceAccountId = version.CreatedByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
                StartsAtUtc = startsAt.ToUniversalTime(),
                EndsAtUtc = endsAt.ToUniversalTime(),
            };
            _dbContext.ResourceAvailability.Add(window);
        }

        window.ResourceDocumentId = resourceId;
        window.StartsAtUtc = startsAt.ToUniversalTime();
        window.EndsAtUtc = endsAt.ToUniversalTime();
    }

    /// <summary>Creates or moves the <see cref="ResourceBlock"/> behind a Maintenance collection's .ics (ADR 0778).</summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="UpsertBookingRowAsync"/>, and short for the reasons a block is simpler than a
    /// booking: it claims exactly one resource, so there is no attendee expansion and no set of sibling claims
    /// to keep agreeing about the window; and it may overlap anything, so there is nothing to reconcile
    /// against its neighbours.
    /// </para>
    /// <para>
    /// It does NOT touch the bookings it catches. Suspension is derived from this row (#1091) — a booking is
    /// suspended exactly while an Active block of its resource overlaps it — so writing a block is the whole
    /// act, and clearing it is the whole revival. That is what keeps this method out of the re-entrancy trap
    /// the booking path fell into: nothing here writes another document, so nothing re-enters the classifier.
    /// </para>
    /// </remarks>
    internal async Task<PendingBookingAudit> UpsertBlockRowAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.RecurrenceRule is not null)
        {
            // Same refusal as a booking, for the same reason: the row models ONE window, and a rule the
            // server does not expand would make the stored block and the displayed one disagree.
            throw new BookingRecurrenceUnsupportedException();
        }

        if (CalendarInstants.Instant(occurrence.DtStart) is not { } startsAt)
        {
            throw new BookingSlotInvalidException("The event carries no DTSTART — a block must name a window.");
        }

        var endsAt = CalendarInstants.Instant(occurrence.DtEnd) ?? startsAt;

        // The resource is the Maintenance collection's parent — containment guarantees the shape (a block
        // lives only in a Maintenance collection, which lives only on a bookable resource).
        var parentId = await _dbContext.Documents
            .Where(d => d.Id == document.ParentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (parentId is not { } resourceId)
        {
            throw new ResourceNotBookableException(
                $"The Maintenance collection holding document {document.Id} has no parent resource to block.");
        }

        var block = await _dbContext.ResourceBlocks
            .FirstOrDefaultAsync(b => b.BlockDocumentId == document.Id, cancellationToken);

        // CLEARING IS A WRITE, not a delete (ADR 0778). STATUS:CANCELLED in the .ics ends the block, which
        // puts the release back at this single door — the same one that placed it — where the right can be
        // checked and the event recorded. Deleting the document still clears the row as a safety net
        // (SyncBlockDocumentsAsync), but that happens inside SaveChanges, where an audit call would be a
        // nested save; recording per-entrance instead is what ADR 0777 exists to reject.
        var wantsCleared = string.Equals(occurrence.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var wasActive = block is { Status: BlockStatus.Active };
        var willBeActive = !wantsCleared;

        await EnsureBlockRightsAsync(version, wasActive, willBeActive, cancellationToken);

        // The bookings this write affects, measured BEFORE the row changes — because suspension is derived,
        // the overlap is only observable while the block's state still says what it said. Afterwards the
        // question is unanswerable, which is exactly why the audit event has to carry the count.
        var affected = await AffectedBookingsAsync(resourceId, startsAt, endsAt, cancellationToken);

        if (block is null)
        {
            block = new ResourceBlock
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                ResourceDocumentId = resourceId,
                BlockDocumentId = document.Id,
                BlockedByUserId = version.CreatedByUserId,
                BlockedByServiceAccountId = version.CreatedByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
                StartsAtUtc = startsAt.ToUniversalTime(),
                EndsAtUtc = endsAt.ToUniversalTime(),
            };
            _dbContext.ResourceBlocks.Add(block);
        }

        // A move between two resources' Maintenance collections re-points the block, exactly as a booking's
        // holding claim follows its document. There is only ever one row, so no ambiguity arises.
        block.ResourceDocumentId = resourceId;
        block.StartsAtUtc = startsAt.ToUniversalTime();
        block.EndsAtUtc = endsAt.ToUniversalTime();
        block.Status = willBeActive ? BlockStatus.Active : BlockStatus.Cleared;

        var resourceName = await _dbContext.Documents
            .Where(d => d.Id == resourceId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? resourceId.ToString();

        // Three facts, three actions — the pair the owner asked for, plus the ordinary edit. Which one this
        // is depends on the TRANSITION, not on the new state alone: a block written again while already
        // active is somebody adjusting a window, not a second grounding.
        var action = (wasActive, willBeActive) switch
        {
            (false, true) => Controllers.AuditActions.BookingSuspended,
            (true, false) => Controllers.AuditActions.BookingRevived,
            _ => Controllers.AuditActions.BookingChanged,
        };

        var effect = action switch
        {
            Controllers.AuditActions.BookingSuspended when affected.Count > 0 => $", suspending {affected.Count} booking(s)",
            Controllers.AuditActions.BookingRevived when affected.Count > 0 => $", reviving {affected.Count} booking(s)",
            Controllers.AuditActions.BookingChanged => string.Empty,
            _ => " (no bookings affected)",
        };

        // The audit event is BUILT here and RECORDED by the caller, after the save — the same rule the
        // booking path learned the hard way (ADR 0777): IAuditRecorder.RecordAsync saves, so recording
        // inline would flush the staged row outside the try/catch that translates an invariant refusal.
        // The people to tell, carried out to the caller with the audit event and delivered after the save for
        // the same reason: INotificationService saves, so notifying inline would flush the staged block row
        // outside the try/catch that translates an invariant refusal (ADR 0777's rule, second application).
        var notice = action switch
        {
            Controllers.AuditActions.BookingSuspended =>
                new PendingBlockNotice(NotificationType.BookingSuspended, document.TenantId, resourceName, startsAt, endsAt, affected),
            Controllers.AuditActions.BookingRevived =>
                new PendingBlockNotice(NotificationType.BookingRevived, document.TenantId, resourceName, startsAt, endsAt, affected),
            _ => null,
        };

        return new PendingBookingAudit(action, $"{startsAt:u}-{endsAt:u} on {resourceName}{effect}", notice);
    }

    /// <summary>Which Active bookings of the resource overlap this window (ADR 0778).</summary>
    /// <remarks>
    /// The number the audit event carries. Suspension is derived and therefore true only while the block is
    /// Active, so this count is the ONLY durable record that those flights were ever stopped — which is why
    /// it is measured at the moment of the act rather than reconstructed later.
    /// </remarks>
    private async Task<List<Guid>> AffectedBookingsAsync(
        Guid resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var from = startsAt.ToUniversalTime();
        var to = endsAt.ToUniversalTime();
        var candidates = await _dbContext.ResourceBookings
            .Where(b => b.ResourceDocumentId == resourceId && b.Status == BookingStatus.Active)
            .Select(b => new { b.BookingDocumentId, b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(cancellationToken);

        // In memory for the reason every range test in this area is: the SQLite provider cannot translate
        // DateTimeOffset range predicates, and one resource's active bookings are few by nature.
        return [.. candidates
            .Where(b => b.StartsAtUtc < to && from < b.EndsAtUtc)
            .Select(b => b.BookingDocumentId)
            .Distinct()];
    }

    /// <summary>
    /// Whether whoever wrote this version may ground the resource, or return it to service (ADR 0778).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved from the VERSION'S CREATOR rather than from an ambient principal accessor, because this door
    /// is reached from paths that have no ambient principal — a worker, a protocol edge, a drop-upload
    /// finalized later. The version records who wrote the bytes on every one of them, which is precisely the
    /// question being asked, and it is the same source the booking path uses to decide who holds a slot.
    /// </para>
    /// <para>
    /// A SERVICE ACCOUNT can ground but never release (owner decision): grounding on suspicion should be
    /// broad — a maintenance integration or an hours counter reaching a limit ought to be able to stop an
    /// aircraft being booked — while "airworthy again" is a certifying act, and nothing here could tell a
    /// considered release from a bug in an integration.
    /// </para>
    /// </remarks>
    private async Task EnsureBlockRightsAsync(
        DocumentVersion version, bool wasActive, bool willBeActive, CancellationToken cancellationToken)
    {
        // Nothing is changing about the resource's availability — an edit to a cleared block's text, say.
        if (wasActive == willBeActive && !willBeActive)
        {
            return;
        }

        var (canBlock, canRelease) = await BlockRightsOfAsync(version, cancellationToken);

        if (willBeActive && !canBlock)
        {
            throw new ResourceBlockRightRequiredException(
                "Taking a resource out of service requires the 'Block resources' right (ADR 0778).");
        }

        if (wasActive && !willBeActive && !canRelease)
        {
            throw new ResourceReleaseRightRequiredException(
                "Returning a resource to service requires the 'Release resources' right (ADR 0778); "
                + "a service account never holds it.");
        }
    }

    private async Task<(bool CanBlock, bool CanRelease)> BlockRightsOfAsync(
        DocumentVersion version, CancellationToken cancellationToken)
    {
        if (version.CreatedByUserId is { } userId)
        {
            // A tenant admin holds both implicitly, as they do every other system right — consistent rather
            // than a new exception carved out for this one pair.
            var rights = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            return (rights.IsTenantAdmin || rights.CanBlockResources, rights.IsTenantAdmin || rights.CanReleaseResources);
        }

        if (version.CreatedByServiceAccountId is { } serviceAccountId)
        {
            var canBlock = await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanBlockResources)
                .FirstOrDefaultAsync(cancellationToken);

            // Release is deliberately unreachable for a machine — there is no column to grant.
            return (canBlock, false);
        }

        // A version with neither creator cannot happen through an admitted write (a CHECK constraint
        // enforces exactly one), so this is the defensive branch: no principal, no authority.
        return (false, false);
    }


    /// <summary>
    /// Tells the people on the affected bookings that their resource went out of service, or came back
    /// (ADR 0778, slice 4b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Filtered against the DERIVED state, after the save.</b> The candidates are the bookings whose window
    /// this block overlaps, but overlap alone does not decide the message: a booking still caught by ANOTHER
    /// active block is not revived when this one clears, and telling its pilot their flight is back on would
    /// be worse than saying nothing. Suspension is derived, so the honest answer only exists once the row has
    /// been written — which is also why this runs here rather than beside the row.
    /// </para>
    /// <para>
    /// <b>Who counts as affected</b> is the claimants plus the booker. A claimant is found through
    /// <c>ResourcePrincipal</c> (ADR 0775) — the table that says a resource document REPRESENTS a person —
    /// because <c>BookedByUserId</c> is the booker, so a student booked by their instructor would not match,
    /// and that is exactly the case that matters. The booker is included as well: they made a commitment on
    /// somebody's behalf and are the one who will have to unmake it.
    /// </para>
    /// <para>
    /// Best-effort, and deliberately last: a failure to notify must not undo a grounding. The block is already
    /// committed and audited by the time this runs.
    /// </para>
    /// </remarks>
    internal async Task NotifyAffectedAsync(PendingBlockNotice notice, CancellationToken cancellationToken)
    {
        if (notice.CandidateBookingIds.Count == 0)
        {
            return;
        }

        var suspended = await BookingSuspension.SuspendedAmongAsync(_dbContext, notice.CandidateBookingIds, cancellationToken);
        var subjects = notice.Type == NotificationType.BookingSuspended
            ? notice.CandidateBookingIds.Where(suspended.Contains).ToList()
            : notice.CandidateBookingIds.Where(id => !suspended.Contains(id)).ToList();
        if (subjects.Count == 0)
        {
            return;
        }

        var claims = await _dbContext.ResourceBookings
            .Where(b => subjects.Contains(b.BookingDocumentId) && b.Status == BookingStatus.Active)
            .Select(b => new { b.BookingDocumentId, b.ResourceDocumentId, b.BookedByUserId })
            .ToListAsync(cancellationToken);

        var resourceIds = claims.Select(c => c.ResourceDocumentId).Distinct().ToList();
        var principals = await _dbContext.ResourcePrincipals
            .Where(p => resourceIds.Contains(p.ResourceDocumentId))
            .Select(p => new { p.ResourceDocumentId, p.UserId })
            .ToListAsync(cancellationToken);
        var personOf = principals.ToDictionary(p => p.ResourceDocumentId, p => p.UserId);

        // One notification per person per booking: two suspended flights are two things to deal with, and
        // these types are non-coalescable for that reason.
        var targets = new HashSet<(Guid UserId, Guid BookingDocumentId)>();
        foreach (var claim in claims)
        {
            if (personOf.TryGetValue(claim.ResourceDocumentId, out var claimantId))
            {
                targets.Add((claimantId, claim.BookingDocumentId));
            }

            if (claim.BookedByUserId is { } bookerId)
            {
                targets.Add((bookerId, claim.BookingDocumentId));
            }
        }

        var window = $"{notice.From:yyyy-MM-dd HH:mm}–{notice.To:HH:mm} UTC";
        var (title, body) = notice.Type == NotificationType.BookingSuspended
            ? ($"{notice.ResourceName} is out of service",
               $"Your booking is on hold: {notice.ResourceName} is unavailable {window}.")
            : ($"{notice.ResourceName} is back in service",
               $"Your booking stands again: {notice.ResourceName} was unavailable {window}.");

        foreach (var (userId, bookingDocumentId) in targets)
        {
            // The tenant is NAMED rather than taken from the ambient accessor: this door is reached from a
            // worker and from protocol edges where none is set, and the ambient overload would drop the
            // notification (loudly now, but still dropped).
            await _notifications.NotifyInTenantAsync(
                notice.TenantId, userId, notice.Type, title, body, bookingDocumentId, cancellationToken);
        }
    }

    /// <summary>What to record once the booking has actually been saved (#1092, ADR 0777).</summary>
    internal sealed record PendingBookingAudit(string Action, string Details, PendingBlockNotice? Notice = null);

    /// <summary>Who to tell that a resource went out of service, or came back (ADR 0778, slice 4b).</summary>
    internal sealed record PendingBlockNotice(
        NotificationType Type, Guid TenantId, string ResourceName,
        DateTimeOffset From, DateTimeOffset To, IReadOnlyList<Guid> CandidateBookingIds);

    /// <summary>The resources claimed by this booking other than the holding one.</summary>
    private static HashSet<Guid> AttendeeResourceIds(List<ResourceBooking> claims, Guid holdingResourceId) =>
        [.. claims.Select(c => c.ResourceDocumentId).Where(id => id != holdingResourceId)];

    private static ResourceBooking NewClaim(
        Document document, DocumentVersion version, Guid resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = document.TenantId,
            ResourceDocumentId = resourceId,
            BookingDocumentId = document.Id,
            StartsAtUtc = startsAt.ToUniversalTime(),
            EndsAtUtc = endsAt.ToUniversalTime(),
            Status = BookingStatus.Active,
            BookedByUserId = version.CreatedByUserId,
            BookedByServiceAccountId = version.CreatedByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// The event's <c>ATTENDEE</c>s become claims of their own (ADR 0776) — a training flight occupies the
    /// aircraft, the student and the instructor, from one PUT by any calendar client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in a controller, because ADR 0744's rule is that EVERY path writing the `.ics` is a
    /// booking act: the bookings endpoint, a drop-upload and a CalDAV PUT all arrive at this one method, and
    /// an expansion living in one of them would be an expansion the other two do not do.
    /// </para>
    /// <para>
    /// An attendee the archive cannot turn into a claim REFUSES the write. Dropping it would book the
    /// resource alone while the author believes a second participant is coming — a booking that looks
    /// complete to everyone and is not.
    /// </para>
    /// <para>
    /// Deliberately NOT RFC 6638 scheduling: no iTIP inbox, no iMIP mail, no PARTSTAT round trip. The
    /// attendee list is read as a statement of who is on the flight, and the conflict invariant answers it.
    /// </para>
    /// </remarks>
    private async Task ReconcileAttendeeClaimsAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence,
        List<ResourceBooking> claims, ResourceBooking holding, DateTimeOffset startsAt, DateTimeOffset endsAt,
        CancellationToken cancellationToken)
    {
        var addresses = occurrence.Attendees
            .Select(a => a.Value?.ToString()?.Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wanted = new HashSet<Guid>();
        foreach (var address in addresses)
        {
            wanted.Add(await ResolveAttendeeResourceAsync(address, cancellationToken));
        }

        // An attendee resolving to the resource that already holds the booking is not a second claim — the
        // organiser inviting the room itself is ordinary, and a duplicate claim would be refused as an overlap.
        wanted.Remove(holding.ResourceDocumentId);

        foreach (var resourceId in wanted.Where(id => claims.All(c => c.ResourceDocumentId != id)))
        {
            _dbContext.ResourceBookings.Add(NewClaim(document, version, resourceId, startsAt, endsAt));
        }

        // Removed from the attendee list means removed from the flight: the claim goes, freeing that person's
        // slot. Cancelled rather than deleted, so the history of who was on it survives (ADR 0735).
        foreach (var dropped in claims.Where(c => c != holding && !wanted.Contains(c.ResourceDocumentId)))
        {
            dropped.Status = BookingStatus.Cancelled;
        }
    }

    /// <summary>The bookable resource standing for this address, or a refusal naming it.</summary>
    /// <remarks>
    /// Two lookups, and both must succeed: the address has to belong to a user of this tenant, and a
    /// <c>ResourcePrincipal</c> has to say which document represents them. A user with no resource is not an
    /// error in the archive — it is somebody nobody has made bookable — but it IS a refusal here, because the
    /// alternative is a flight silently missing a participant.
    ///
    /// Matched on <c>NormalizedEmail</c>, which is what makes the comparison case-insensitive (ADR 0150): an
    /// invitation addressed to Anna@school and a user stored as anna@school are the same person, and a client
    /// composing the address from a display name will not match the stored casing.
    /// </remarks>
    private async Task<Guid> ResolveAttendeeResourceAsync(string address, CancellationToken cancellationToken)
    {
        var normalized = address.ToUpperInvariant();
        var userId = await _dbContext.Users
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is not { } id)
        {
            throw new BookingAttendeeUnknownException(address);
        }

        var resourceIds = await _dbContext.ResourcePrincipals
            .Where(p => p.UserId == id)
            .Select(p => p.ResourceDocumentId)
            .ToListAsync(cancellationToken);

        return resourceIds.Count switch
        {
            1 => resourceIds[0],
            0 => throw new BookingAttendeeUnknownException(address),

            // Two resources for one person is legal (the mapping is unique on the RESOURCE, not the user) and
            // leaves this with no way to choose. Refused rather than guessed: booking the wrong one of
            // somebody's two calendars is a mistake nobody would look for.
            _ => throw new BookingAttendeeAmbiguousException(address, resourceIds.Count),
        };
    }
}
