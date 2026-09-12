using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

// The booking primitive's SaveChanges half (ADRs 0735/0744), in its own partial so the main file stays
// under the class-size ceiling: the invariant that guards the slot, and the sync that makes the booking
// DOCUMENT's lifecycle drive the row — the two must run in this order (sync first), so a document change
// that reactivates or moves a claim is validated in the same save that made it.
public partial class SimplArchiveDbContext
{
    // The document's lifecycle drives the row (ADR 0744: the booking IS the .ics). Soft- or hard-deleting
    // a Room-booking document cancels its claim — which is what clears the slot on EVERY delete path (API
    // cancel, recycle bin, CalDAV DELETE) without each path knowing about bookings. Restoring one is a
    // REBOOK: the row goes back to Active here and straight through the overlap invariant below, so a slot
    // taken in the meantime refuses the restore instead of double-booking. A move between two rooms'
    // Schedules re-points the claim at the new room (containment has already refused any other target).
    private async Task SyncBookingDocumentsAsync(CancellationToken cancellationToken)
    {
        var candidates = ChangeTracker.Entries<Document>()
            .Where(e => e.State == EntityState.Deleted
                || (e.State == EntityState.Modified
                    && (e.Property(d => d.DeletedAt).IsModified || e.Property(d => d.ParentId).IsModified)))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var entry in candidates)
        {
            var document = entry.Entity;
            if (document.MaskVersionId is not { } maskVersionId)
            {
                continue;
            }

            // IgnoreQueryFilters throughout: the write may come from a worker or protocol edge with no
            // ambient tenant, where the tenant filter would silently match nothing (the auth-lookup lesson).
            var isBooking = await MaskVersions.IgnoreQueryFilters()
                .AnyAsync(v => v.Id == maskVersionId && v.MaskId == WellKnownMaskIds.Booking, cancellationToken);
            if (!isBooking)
            {
                continue;
            }

            // EVERY claim of this document, not the first (ADR 0774): a booking may hold several — a
            // training flight claims the aircraft, the student and the instructor — and cancelling one
            // while leaving the others Active would free the aircraft and keep two people committed to a
            // flight that no longer exists.
            var rows = await ResourceBookings.IgnoreQueryFilters()
                .Where(b => b.BookingDocumentId == document.Id)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                continue; // no claim yet — the classifier creates the row when the bytes land
            }

            if (entry.State == EntityState.Deleted || document.DeletedAt is not null)
            {
                foreach (var row in rows.Where(r => r.Status == BookingStatus.Active))
                {
                    row.Status = BookingStatus.Cancelled;
                }
            }
            else if (entry.Property(d => d.DeletedAt).IsModified && document.DeletedAt is null)
            {
                // Restore is a REBOOK for every claim, and each goes back through the overlap invariant —
                // so a restore is refused if ANY of the three slots was taken in the meantime, which is
                // right: the flight cannot come back for two of its participants.
                foreach (var row in rows.Where(r => r.Status == BookingStatus.Cancelled))
                {
                    row.Status = BookingStatus.Active;
                }
            }

            if (entry.State == EntityState.Modified
                && entry.Property(d => d.ParentId).IsModified
                && document.ParentId is { } newScheduleId)
            {
                await RepointContainingClaimAsync(entry, rows, newScheduleId, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Moving the booking document between two Schedules re-points the claim on the resource that HOLDS the
    /// document — and only that one (ADR 0774).
    /// </summary>
    /// <remarks>
    /// With one claim per booking this was unambiguous. With several it is not: moving a training flight
    /// from one aircraft's Schedule to another changes which AIRCRAFT is flown and says nothing about who
    /// is flying it, so re-pointing every claim would silently reassign the student and the instructor to
    /// the new aircraft as if they were rooms.
    ///
    /// The claim to move is found from the parent's ORIGINAL value rather than by guessing which row looks
    /// like a resource: the document was in the old Schedule, so exactly the claim whose resource owns that
    /// Schedule is the containing one. A document with no such claim (a person's leg somehow moved on its
    /// own) is left alone rather than repaired by assumption.
    /// </remarks>
    private async Task RepointContainingClaimAsync(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Document> entry,
        List<ResourceBooking> rows,
        Guid newScheduleId,
        CancellationToken cancellationToken)
    {
        var newResourceId = await Documents.IgnoreQueryFilters()
            .Where(d => d.Id == newScheduleId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (newResourceId is not { } resourceId)
        {
            return;
        }

        if (rows.Count == 1)
        {
            // The single-claim case, unchanged: the one claim is the containing one by definition.
            if (rows[0].ResourceDocumentId != resourceId)
            {
                rows[0].ResourceDocumentId = resourceId;
            }

            return;
        }

        if (entry.Property(d => d.ParentId).OriginalValue is not { } oldScheduleId)
        {
            return;
        }

        var oldResourceId = await Documents.IgnoreQueryFilters()
            .Where(d => d.Id == oldScheduleId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldResourceId is not { } previous)
        {
            return;
        }

        var containing = rows.FirstOrDefault(r => r.ResourceDocumentId == previous);
        if (containing is not null && containing.ResourceDocumentId != resourceId)
        {
            containing.ResourceDocumentId = resourceId;
        }
    }

    // The booking primitive's invariants (ADR 0735), at the one door every write path uses: a booking's
    // resource must wear a bookable mask, its slot must have extent, and no two Active bookings of one
    // resource may overlap. The overlap rule lives HERE rather than as a database constraint because a
    // range-exclusion constraint is Postgres-only and the model must run on SQLite too (provider parity);
    // rather than in a service because appointments/bookings are writable from more than one path and a
    // rule enforced at one entrance is not a rule.
    private async Task ValidateResourceBookingsAsync(CancellationToken cancellationToken)
    {
        var changed = ChangeTracker.Entries<ResourceBooking>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .Where(b => b.Status == BookingStatus.Active)
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        // Everything Active already tracked this save (two bookings added together must conflict with each
        // other, not just with stored rows). ChangeTracker enumeration bypasses query filters, same as the
        // MaskVersion auto-numbering (ADR 0198), so the tenant scoping below is explicit.
        var pendingActive = ChangeTracker.Entries<ResourceBooking>()
            .Where(e => e.State is not EntityState.Deleted)
            .Select(e => e.Entity)
            .Where(b => b.Status == BookingStatus.Active)
            .ToList();

        foreach (var booking in changed)
        {
            if (booking.StartsAtUtc >= booking.EndsAtUtc)
            {
                throw BookingInvariantException.SlotWithoutExtent(booking.StartsAtUtc, booking.EndsAtUtc);
            }

            // The resource's mask must declare bookability — the shared document -> version -> mask walk
            // (see IsBookableAsync in the Blocks partial; the block invariant asks the same question, and
            // two copies of it is how one of them ends up accepting a resource the other refuses).
            if (!await IsBookableAsync(booking.TenantId, booking.ResourceDocumentId, cancellationToken))
            {
                throw BookingInvariantException.NotBookable(booking.ResourceDocumentId);
            }

            // ...and it must not be out of service for any part of the slot (ADR 0778). Refused rather than
            // suspended, because this booking does not exist yet: a booking made INTO a known grounding is a
            // mistake to correct now, while one caught by a block placed later is a commitment to suspend and
            // notify about. Same rule, opposite sides of the block's placement — and the reason the refusal
            // is its own kind is that the remedies differ: another hour fixes a taken slot, and nothing fixes
            // an aircraft that is not airworthy.
            var blocked = (await OverlappingBlocksAsync(
                booking.TenantId, booking.ResourceDocumentId, booking.StartsAtUtc, booking.EndsAtUtc, cancellationToken))
                .FirstOrDefault();
            if (blocked is not null)
            {
                throw BookingInvariantException.ResourceBlocked(
                    booking.StartsAtUtc, booking.EndsAtUtc, blocked.StartsAtUtc, blocked.EndsAtUtc);
            }

            // ...and the resource must actually be ON OFFER for the whole slot — but only if it has offered
            // anything at all (#1124).
            //
            // Reported from use: a window was published for a room, an appointment was made that STARTED
            // inside it and ENDED outside, and it was accepted. Core used to leave this entirely to an
            // industry module's booking rule, which is why a plain installation had an Availability
            // collection that did nothing — publish a window, and nothing anywhere honoured it.
            //
            // The gate is "has published at least one window", so a resource that never used availability
            // books exactly as before and cannot be surprised by a rule it never opted into, while one that
            // HAS published is taken to mean it. A module's own rule still layers on top: this answers "is
            // the resource on offer", not "did this particular person consent", which stays the module's
            // question (ADR 0780).
            //
            // Coverage by a SINGLE window, per WindowsCoveringAsync — two adjacent windows are not merged
            // into one offer, because that is an inference about somebody's intent the core should not make.
            var hasOffered = await ResourceAvailability.IgnoreQueryFilters()
                .AnyAsync(a => a.TenantId == booking.TenantId
                    && a.ResourceDocumentId == booking.ResourceDocumentId
                    && a.Status == AvailabilityStatus.Offered, cancellationToken);
            if (hasOffered
                && (await WindowsCoveringAsync(
                    booking.TenantId, booking.ResourceDocumentId, booking.StartsAtUtc, booking.EndsAtUtc, cancellationToken))
                    .Count == 0)
            {
                throw BookingInvariantException.NotOffered(booking.StartsAtUtc, booking.EndsAtUtc);
            }

            // Overlap against Active rows of the same resource ([start, end) semantics: touching slots are
            // fine), excluding self; anything already tracked is judged from its tracked state. The time
            // comparison runs IN MEMORY: the SQLite provider cannot translate DateTimeOffset range
            // predicates, and the candidate set — one resource's active bookings — is small by nature,
            // reached through the (TenantId, ResourceDocumentId, StartsAtUtc) index.
            var clash = (await OverlappingClaimsAsync(
                    booking.TenantId, booking.ResourceDocumentId, booking.StartsAtUtc, booking.EndsAtUtc,
                    excludingClaimId: booking.Id, cancellationToken))
                .FirstOrDefault();
            if (clash is not null)
            {
                throw BookingInvariantException.SlotTaken(
                    booking.StartsAtUtc, booking.EndsAtUtc, clash.StartsAtUtc, clash.EndsAtUtc);
            }

            // Every claim of one document names the same window (ADR 0774) — what replaces the uniqueness
            // that used to make this true by construction. Judged against tracked state as well as stored
            // rows, so three claims added together are checked against each other and not only against what
            // is already in the database.
            if (booking.BookingDocumentId == Guid.Empty)
            {
                continue; // no document to be a claim OF — every such row would look like every other's sibling
            }

            var trackedIds = pendingActive.Select(b => b.Id).ToList();
            var sibling = (await ResourceBookings.IgnoreQueryFilters()
                    .Where(b => b.TenantId == booking.TenantId
                        && b.BookingDocumentId == booking.BookingDocumentId
                        && b.Status == BookingStatus.Active   // a Cancelled claim is history, not a disagreement
                        && b.Id != booking.Id
                        && !trackedIds.Contains(b.Id))
                    .ToListAsync(cancellationToken))
                .Concat(pendingActive.Where(b =>
                    b.Id != booking.Id
                    && b.TenantId == booking.TenantId
                    && b.BookingDocumentId == booking.BookingDocumentId))
                .FirstOrDefault(b => b.StartsAtUtc != booking.StartsAtUtc || b.EndsAtUtc != booking.EndsAtUtc);
            if (sibling is not null)
            {
                throw BookingInvariantException.ClaimsDisagreeOnSlot(
                    booking.BookingDocumentId, booking.StartsAtUtc, booking.EndsAtUtc,
                    sibling.StartsAtUtc, sibling.EndsAtUtc);
            }
        }
    }

    /// <summary>
    /// The ACTIVE claims of one resource that overlap a slot (ADR 0735), earliest first.
    /// </summary>
    /// <remarks>
    /// Stated once because it is now asked twice: the invariant asks it of a booking being written, and
    /// <c>IsFreeAsync</c> asks it of a resource a module is about to offer somebody (ADR 0785). Two copies of
    /// a range test is how one of them ends up treating touching slots as a clash while the other does not —
    /// the same reason <see cref="OverlappingBlocksAsync"/> exists rather than being inlined twice.
    ///
    /// Half-open <c>[start, end)</c>: a booking ending exactly when another begins is not an overlap. Tracked
    /// rows are judged from their tracked state, so claims added in one save are checked against each other
    /// and not only against what is stored. The range test runs IN MEMORY because the SQLite provider cannot
    /// translate <see cref="DateTimeOffset"/> predicates, and one resource's active claims are few.
    /// </remarks>
    internal async Task<List<ResourceBooking>> OverlappingClaimsAsync(
        Guid tenantId, Guid resourceDocumentId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        Guid? excludingClaimId, CancellationToken cancellationToken)
    {
        var tracked = ChangeTracker.Entries<ResourceBooking>()
            .Where(e => e.State is not EntityState.Deleted)
            .Select(e => e.Entity)
            .Where(b => b.Status == BookingStatus.Active
                && b.TenantId == tenantId
                && b.ResourceDocumentId == resourceDocumentId
                && b.Id != excludingClaimId)
            .ToList();
        var trackedIds = tracked.Select(b => b.Id).ToList();

        var stored = await ResourceBookings.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId
                && b.ResourceDocumentId == resourceDocumentId
                && b.Status == BookingStatus.Active
                && b.Id != excludingClaimId
                && !trackedIds.Contains(b.Id))
            .ToListAsync(cancellationToken);

        return [.. stored.Concat(tracked)
            .Where(b => b.StartsAtUtc < endsAt && startsAt < b.EndsAtUtc)
            .OrderBy(b => b.StartsAtUtc)];
    }
}
