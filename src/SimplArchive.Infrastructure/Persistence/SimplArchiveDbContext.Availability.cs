using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

// The OFFERED-time half of the booking primitive (ADR 0780), in its own partial beside the booking and block
// ones so each file carries one rule and the class stays under the size ceiling.
//
// What is deliberately absent here is an overlap invariant of any kind. A window may overlap a booking —
// that overlap IS the "spoken for" answer (#1093) — and may overlap another window, because publishing more
// time is not a conflict. The only rules a window has are that it has extent and that its resource is
// bookable at all.
public partial class SimplArchiveDbContext
{
    // The window document's lifecycle drives the row, exactly as a booking's and a block's do: deleting the
    // .ics withdraws the offer on every delete path without any of them knowing what availability is.
    private async Task SyncAvailabilityDocumentsAsync(CancellationToken cancellationToken)
    {
        var candidates = ChangeTracker.Entries<Document>()
            .Where(e => e.State == EntityState.Deleted
                || (e.State == EntityState.Modified && e.Property(d => d.DeletedAt).IsModified))
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

            var isWindow = await MaskVersions.IgnoreQueryFilters()
                .AnyAsync(v => v.Id == maskVersionId && v.MaskId == WellKnownMaskIds.AvailabilityWindow, cancellationToken);
            if (!isWindow)
            {
                continue;
            }

            var row = await ResourceAvailability.IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.WindowDocumentId == document.Id, cancellationToken);
            if (row is null)
            {
                continue; // no row yet — the classifier creates it when the bytes land
            }

            if (entry.State == EntityState.Deleted || document.DeletedAt is not null)
            {
                row.Status = AvailabilityStatus.Withdrawn;
            }
            else if (entry.Property(d => d.DeletedAt).IsModified && document.DeletedAt is null)
            {
                row.Status = AvailabilityStatus.Offered;
            }
        }
    }

    private async Task ValidateResourceAvailabilityAsync(CancellationToken cancellationToken)
    {
        var changed = ChangeTracker.Entries<ResourceAvailability>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var window in changed)
        {
            if (window.StartsAtUtc >= window.EndsAtUtc)
            {
                throw BookingInvariantException.WindowWithoutExtent(window.StartsAtUtc, window.EndsAtUtc);
            }

            if (!await IsBookableAsync(window.TenantId, window.ResourceDocumentId, cancellationToken))
            {
                throw BookingInvariantException.NotBookable(window.ResourceDocumentId);
            }
        }
    }

    /// <summary>The Offered windows of a resource that cover a slot entirely (ADR 0780).</summary>
    /// <remarks>
    /// <b>Covers, not overlaps</b> — and the difference is the point. A module asking "may this be booked?"
    /// is asking whether the whole slot was offered; a window ending at 12:00 does not consent to a flight
    /// running until 13:00 merely because the two touch. Overlap answers a different question (is this hour
    /// spoken for), and conflating the two would let half an offer authorise a whole booking.
    ///
    /// Coverage by a SINGLE window, deliberately: two adjacent windows arguably cover a slot between them,
    /// but treating them as one offer is an inference about somebody's intent that the core should not make.
    /// A resource offering continuous time can publish it as one window.
    /// </remarks>
    /// <remarks>
    /// PUBLIC, unlike its block sibling: the overlap test for blocks is an invariant this class enforces and
    /// nobody outside asks, while "was this slot offered?" is a question the module's booking rule asks
    /// before it books. Keeping it internal would have meant the module re-deriving coverage against the
    /// rows, which is how two answers to one question start disagreeing about whether touching counts.
    /// </remarks>
    public async Task<List<ResourceAvailability>> WindowsCoveringAsync(
        Guid tenantId, Guid resourceDocumentId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var from = startsAt.ToUniversalTime();
        var to = endsAt.ToUniversalTime();

        var stored = await ResourceAvailability.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId
                && a.ResourceDocumentId == resourceDocumentId
                && a.Status == AvailabilityStatus.Offered)
            .ToListAsync(cancellationToken);

        // In memory for the reason every range test in this area is: the SQLite provider cannot translate
        // DateTimeOffset range predicates, and one resource's windows are few by nature.
        return [.. stored.Where(a => a.StartsAtUtc <= from && to <= a.EndsAtUtc).OrderBy(a => a.StartsAtUtc)];
    }
}
