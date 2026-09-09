using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

// The maintenance-block half of the booking primitive (ADR 0778), in its own partial beside the booking one
// so neither file carries both rules and the class stays under the size ceiling.
//
// A block is the booking's mirror image, and the asymmetries are the point: blocks MAY overlap each other
// (two defects on one aircraft are two findings, and refusing the second loses one), while a booking may not
// overlap a block at all. Suspension of what a block catches is DERIVED, never stored (#1091) — so there is
// no suspension pass here, and no revival pass either. Clearing the block IS the revival.
public partial class SimplArchiveDbContext
{
    // The block document's lifecycle drives the row, exactly as a booking's does: deleting the .ics in a
    // Maintenance collection clears the block on every delete path (API, recycle bin, CalDAV DELETE) without
    // any of them knowing what a block is, and restoring it grounds the resource again.
    //
    // Restoring is deliberately NOT put back through an overlap check the way a booking's restore is: a block
    // has nothing to conflict with. What it may now catch is bookings made while it was cleared, and those
    // become suspended by the derived rule the moment the row is Active again — which is the correct outcome
    // and needs no code here.
    private async Task SyncBlockDocumentsAsync(CancellationToken cancellationToken)
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

            // IgnoreQueryFilters throughout: the write may come from a worker or protocol edge with no
            // ambient tenant, where the filter's TenantId == null predicate matches nothing silently.
            var isBlock = await MaskVersions.IgnoreQueryFilters()
                .AnyAsync(v => v.Id == maskVersionId && v.MaskId == WellKnownMaskIds.MaintenanceBlock, cancellationToken);
            if (!isBlock)
            {
                continue;
            }

            var row = await ResourceBlocks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.BlockDocumentId == document.Id, cancellationToken);
            if (row is null)
            {
                continue; // no row yet — the classifier creates it when the bytes land
            }

            if (entry.State == EntityState.Deleted || document.DeletedAt is not null)
            {
                row.Status = BlockStatus.Cleared;
            }
            else if (entry.Property(d => d.DeletedAt).IsModified && document.DeletedAt is null)
            {
                row.Status = BlockStatus.Active;
            }
        }
    }

    // A block's own invariants: a window with extent, against a resource that is actually bookable. There is
    // deliberately no overlap rule — see the file header.
    private async Task ValidateResourceBlocksAsync(CancellationToken cancellationToken)
    {
        var changed = ChangeTracker.Entries<ResourceBlock>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var block in changed)
        {
            if (block.StartsAtUtc >= block.EndsAtUtc)
            {
                throw BookingInvariantException.BlockWithoutExtent(block.StartsAtUtc, block.EndsAtUtc);
            }

            if (!await IsBookableAsync(block.TenantId, block.ResourceDocumentId, cancellationToken))
            {
                throw BookingInvariantException.NotBookable(block.ResourceDocumentId);
            }
        }
    }

    /// <summary>
    /// The Active blocks of a resource that overlap a window — the one query both halves of ADR 0778's rule
    /// ask, from opposite directions.
    /// </summary>
    /// <remarks>
    /// The refusal on create asks it of a booking being written; the derived suspension asks it of a booking
    /// being read. Stating it once is what stops the two drifting into disagreeing about, say, whether a
    /// block ending exactly at a booking's start counts — it does not, because both ranges are half-open
    /// <c>[start, end)</c>, the same semantics bookings already use against each other.
    ///
    /// The time comparison runs IN MEMORY for the same reason the booking overlap does: the SQLite provider
    /// cannot translate <see cref="DateTimeOffset"/> range predicates, and one resource's active blocks are
    /// few by nature — reached through the (TenantId, ResourceDocumentId, StartsAtUtc) index.
    /// </remarks>
    internal async Task<List<ResourceBlock>> OverlappingBlocksAsync(
        Guid tenantId, Guid resourceDocumentId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        // Tracked rows too, so a block added in the same save as a booking is seen by the booking's check —
        // the ChangeTracker enumeration bypasses query filters, so the tenant scoping below is explicit.
        var tracked = ChangeTracker.Entries<ResourceBlock>()
            .Where(e => e.State is not EntityState.Deleted)
            .Select(e => e.Entity)
            .Where(b => b.TenantId == tenantId && b.ResourceDocumentId == resourceDocumentId && b.Status == BlockStatus.Active)
            .ToList();
        var trackedIds = tracked.Select(b => b.Id).ToList();

        var stored = await ResourceBlocks.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId
                && b.ResourceDocumentId == resourceDocumentId
                && b.Status == BlockStatus.Active
                && !trackedIds.Contains(b.Id))
            .ToListAsync(cancellationToken);

        return [.. stored.Concat(tracked)
            .Where(b => b.StartsAtUtc < endsAt && startsAt < b.EndsAtUtc)
            .OrderBy(b => b.StartsAtUtc)];
    }

    /// <summary>The resource's mask declares <c>IsBookable</c> — the walk document → version → mask.</summary>
    /// <remarks>
    /// The capability lives on the Mask identity rather than on a version, so it does not change when a
    /// version is cut. Extracted because the booking invariant and the block invariant ask the same question
    /// of the same walk, and two copies of it is how one of them ends up accepting a resource the other
    /// refuses.
    /// </remarks>
    private async Task<bool> IsBookableAsync(Guid tenantId, Guid resourceDocumentId, CancellationToken cancellationToken) =>
        await Documents.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.Id == resourceDocumentId)
            .Join(MaskVersions.IgnoreQueryFilters(),
                d => new { d.TenantId, Id = d.MaskVersionId ?? Guid.Empty },
                v => new { v.TenantId, v.Id },
                (d, v) => v)
            .Join(Masks.IgnoreQueryFilters(),
                v => new { v.TenantId, Id = v.MaskId },
                m => new { m.TenantId, m.Id },
                (v, m) => m.IsBookable)
            .FirstOrDefaultAsync(cancellationToken);
}
