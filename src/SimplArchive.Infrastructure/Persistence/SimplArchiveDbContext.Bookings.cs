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
    private async Task SyncRoomBookingDocumentsAsync(CancellationToken cancellationToken)
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
                .AnyAsync(v => v.Id == maskVersionId && v.MaskId == WellKnownMaskIds.RoomBooking, cancellationToken);
            if (!isBooking)
            {
                continue;
            }

            var row = await ResourceBookings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.BookingDocumentId == document.Id, cancellationToken);
            if (row is null)
            {
                continue; // no claim yet — the classifier creates the row when the bytes land
            }

            if (entry.State == EntityState.Deleted || document.DeletedAt is not null)
            {
                if (row.Status == BookingStatus.Active)
                {
                    row.Status = BookingStatus.Cancelled;
                }
            }
            else if (entry.Property(d => d.DeletedAt).IsModified && document.DeletedAt is null
                && row.Status == BookingStatus.Cancelled)
            {
                row.Status = BookingStatus.Active;
            }

            if (entry.State == EntityState.Modified
                && entry.Property(d => d.ParentId).IsModified
                && document.ParentId is { } newScheduleId)
            {
                var newRoomId = await Documents.IgnoreQueryFilters()
                    .Where(d => d.Id == newScheduleId)
                    .Select(d => d.ParentId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (newRoomId is { } roomId && row.ResourceDocumentId != roomId)
                {
                    row.ResourceDocumentId = roomId;
                }
            }
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

            // The resource's mask must declare bookability. A document points at a MaskVersion; the
            // capability lives on the Mask identity (it does not change when a version is cut), so the walk
            // is document -> version -> mask. IgnoreQueryFilters because this must also hold for writers
            // with no ambient tenant (seeders, workers) whose filter predicate would silently match nothing.
            var isBookable = await Documents.IgnoreQueryFilters()
                .Where(d => d.TenantId == booking.TenantId && d.Id == booking.ResourceDocumentId)
                .Join(MaskVersions.IgnoreQueryFilters(),
                    d => new { d.TenantId, Id = d.MaskVersionId ?? Guid.Empty },
                    v => new { v.TenantId, v.Id },
                    (d, v) => v)
                .Join(Masks.IgnoreQueryFilters(),
                    v => new { v.TenantId, Id = v.MaskId },
                    m => new { m.TenantId, m.Id },
                    (v, m) => m.IsBookable)
                .FirstOrDefaultAsync(cancellationToken);
            if (!isBookable)
            {
                throw BookingInvariantException.NotBookable(booking.ResourceDocumentId);
            }

            // Overlap against Active rows of the same resource ([start, end) semantics: touching slots are
            // fine), excluding self; anything already tracked is judged from its tracked state. The time
            // comparison runs IN MEMORY: the SQLite provider cannot translate DateTimeOffset range
            // predicates, and the candidate set — one resource's active bookings — is small by nature,
            // reached through the (TenantId, ResourceDocumentId, StartsAtUtc) index.
            var trackedIds = pendingActive.Select(b => b.Id).ToList();
            var stored = await ResourceBookings.IgnoreQueryFilters()
                .Where(b => b.TenantId == booking.TenantId
                    && b.ResourceDocumentId == booking.ResourceDocumentId
                    && b.Status == BookingStatus.Active
                    && b.Id != booking.Id
                    && !trackedIds.Contains(b.Id))
                .ToListAsync(cancellationToken);
            var clash = stored
                .Concat(pendingActive.Where(b =>
                    b.Id != booking.Id
                    && b.TenantId == booking.TenantId
                    && b.ResourceDocumentId == booking.ResourceDocumentId))
                .Where(b => b.StartsAtUtc < booking.EndsAtUtc && booking.StartsAtUtc < b.EndsAtUtc)
                .OrderBy(b => b.StartsAtUtc)
                .FirstOrDefault();
            if (clash is not null)
            {
                throw BookingInvariantException.SlotTaken(
                    booking.StartsAtUtc, booking.EndsAtUtc, clash.StartsAtUtc, clash.EndsAtUtc);
            }
        }
    }
}
