using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Infrastructure.Persistence;

// The collections a bookable resource holds, created when it BECOMES bookable (#1097).
//
// ADR 0744 says "book a room from your own calendar client" is real. It was real only after somebody booked
// the room in the app once: BookingsController created the Schedule lazily on first booking, and nothing
// created Maintenance or Availability at all. So a calendar client subscribing to a freshly created aircraft
// saw nothing to PUT into, and publishing availability — which ADR 0780 says is done from the phone you
// already carry — had nowhere to land.
//
// HERE rather than at the write paths, because there are at least eight that assign a mask (the metadata
// endpoint, the finalizer, the importer, two provisioners, the seeders, the module facade) and a rule
// maintained at one entrance is a rule the others silently do not maintain. That is not a hypothetical: the
// ResourcePrincipal mapping (ADR 0779) keyed on the wrong event and produced NOTHING for any real caller
// until it was driven against a live stack.
public partial class SimplArchiveDbContext
{
    private async Task ProvisionBookableCollectionsAsync(CancellationToken cancellationToken)
    {
        // An ADDED document counts whenever it carries a mask at all: EF reports IsModified as FALSE for a
        // new entity's properties, so testing that alone misses every document created WITH its mask — which
        // is how the children endpoint makes a typed folder, and why the first version of this provisioned
        // nothing for a freshly created room.
        var becameBookable = ChangeTracker.Entries<Document>()
            .Where(e => e.Entity.MaskVersionId is not null
                && (e.State == EntityState.Added
                    || (e.State == EntityState.Modified && e.Property(d => d.MaskVersionId).IsModified)))
            .Select(e => e.Entity)
            .ToList();
        if (becameBookable.Count == 0)
        {
            return;
        }

        // The tracked entity wins over a query: the new mask is not saved yet, so a projection would read the
        // mask the document had BEFORE this save — the exact trap the principal mapping's first fix fell into.
        var versionIds = becameBookable.Select(d => d.MaskVersionId!.Value).Distinct().ToList();
        var bookableVersions = await MaskVersions.IgnoreQueryFilters()
            .Where(v => versionIds.Contains(v.Id))
            .Join(Masks.IgnoreQueryFilters(),
                v => new { v.TenantId, Id = v.MaskId },
                m => new { m.TenantId, m.Id },
                (v, m) => new { VersionId = v.Id, m.IsBookable })
            .Where(x => x.IsBookable)
            .Select(x => x.VersionId)
            .ToListAsync(cancellationToken);
        if (bookableVersions.Count == 0)
        {
            return;
        }

        // Derived from the kind table by extension, never a hand-written list of three: that list is what
        // left Maintenance and Availability out of the dav-collections listing (ADR 0786's sibling lesson),
        // and a kind added later must arrive here without anybody remembering this file.
        var kinds = DavCollectionKinds.All.Where(k => k.Extension == ".ics" && k.FolderMaskId != Domain.Masks.WellKnownMaskIds.Calendar).ToList();

        foreach (var resource in becameBookable.Where(d => bookableVersions.Contains(d.MaskVersionId!.Value)))
        {
            foreach (var kind in kinds)
            {
                await EnsureCollectionAsync(resource, kind.FolderMaskId, cancellationToken);
            }
        }
    }

    /// <summary>One collection under a resource, created only when it is not already there.</summary>
    /// <remarks>
    /// Idempotent by NAME as well as by mask: a resource may already carry a hand-made "Schedule" folder from
    /// before this existed, and adding a second would fail the sibling-name invariant — turning what should be
    /// a silent upgrade into a refused write on somebody's unrelated edit.
    /// </remarks>
    private async Task EnsureCollectionAsync(Document resource, Guid folderMaskId, CancellationToken cancellationToken)
    {
        var maskVersion = await MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == resource.TenantId && v.MaskId == folderMaskId && v.IsCurrent)
            .Select(v => new { v.Id, v.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (maskVersion is null)
        {
            return; // the tenant has not been seeded with this well-known mask yet; the heal will catch up
        }

        var taken = await Documents.IgnoreQueryFilters()
            .AnyAsync(d => d.ParentId == resource.Id && d.DeletedAt == null
                && (d.Name == maskVersion.Name || d.MaskVersionId == maskVersion.Id), cancellationToken);
        if (taken || ChangeTracker.Entries<Document>().Any(e =>
                e.State is not EntityState.Deleted
                && e.Entity.ParentId == resource.Id
                && (e.Entity.Name == maskVersion.Name || e.Entity.MaskVersionId == maskVersion.Id)))
        {
            return;
        }

        Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = resource.TenantId,
            ParentId = resource.Id,
            Name = maskVersion.Name,
            MaskVersionId = maskVersion.Id,
            CreatedByUserId = resource.CreatedByUserId,
            CreatedByServiceAccountId = resource.CreatedByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageFolderId = Guid.NewGuid(),
        });
    }
}
