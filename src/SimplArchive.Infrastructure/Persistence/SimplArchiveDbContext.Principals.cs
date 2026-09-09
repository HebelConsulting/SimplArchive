using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

// Who a document REPRESENTS (ADR 0779, ABI 0.13), maintained from the field its mask declares.
//
// Here rather than at a write path because there are several — a module creating a dossier, the metadata
// endpoint editing its e-mail, an import, a heal — and a mapping maintained at one entrance is a mapping the
// other entrances silently do not maintain. That is the same reasoning that put the booking invariants here,
// and the same failure it avoids: this one fails INVISIBLY, since a document representing nobody looks
// exactly like a document nobody has claimed yet.
public partial class SimplArchiveDbContext
{
    private async Task SyncResourcePrincipalsAsync(CancellationToken cancellationToken)
    {
        // Any field value written this save — the declaration names a FIELD, so a change to that field is
        // the only thing that can change whom the document represents.
        var written = ChangeTracker.Entries<FieldValue>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();
        if (written.Count == 0)
        {
            return;
        }

        // IgnoreQueryFilters throughout: a module's seeder and a worker write with no ambient tenant, where
        // the filter's TenantId == null predicate matches nothing — silently, which here would mean the
        // mapping is simply never written on exactly the paths that create dossiers.
        var documentIds = written.Select(v => v.DocumentId).Distinct().ToList();
        var documents = await Documents.IgnoreQueryFilters()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.TenantId, d.MaskVersionId })
            .ToListAsync(cancellationToken);
        if (documents.Count == 0)
        {
            return;
        }

        var versionIds = documents.Where(d => d.MaskVersionId != null).Select(d => d.MaskVersionId!.Value).Distinct().ToList();
        var declaring = await MaskVersions.IgnoreQueryFilters()
            .Where(v => versionIds.Contains(v.Id))
            .Join(Masks.IgnoreQueryFilters(),
                v => new { v.TenantId, Id = v.MaskId },
                m => new { m.TenantId, m.Id },
                (v, m) => new { VersionId = v.Id, m.RepresentsPrincipalField })
            .Where(x => x.RepresentsPrincipalField != null)
            .ToListAsync(cancellationToken);
        if (declaring.Count == 0)
        {
            return;
        }

        var fieldOf = declaring.ToDictionary(x => x.VersionId, x => x.RepresentsPrincipalField!);
        var fieldIds = written.Select(v => v.FieldDefinitionId).Distinct().ToList();
        var fieldNames = await FieldDefinitions.IgnoreQueryFilters()
            .Where(f => fieldIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        var nameOf = fieldNames.ToDictionary(f => f.Id, f => f.Name);

        foreach (var document in documents)
        {
            if (document.MaskVersionId is not { } versionId || !fieldOf.TryGetValue(versionId, out var declaredField))
            {
                continue;
            }

            var value = written.FirstOrDefault(v => v.DocumentId == document.Id
                && nameOf.TryGetValue(v.FieldDefinitionId, out var name) && name == declaredField);
            if (value is null)
            {
                continue; // this save touched other fields of the document, not the declared one
            }

            await UpsertPrincipalAsync(document.Id, document.TenantId, value.Value, cancellationToken);
        }
    }

    /// <summary>Points the document at the user the address names, or at nobody.</summary>
    /// <remarks>
    /// An address resolving to no user leaves the document representing NOBODY rather than failing the write:
    /// a dossier filed before its pilot has an account is an ordinary, correct state, and refusing it would
    /// make the module's own provisioning order a constraint the core imposes. The existing mapping is
    /// REMOVED in that case rather than left standing — a stale pointer to the previous person would make
    /// somebody else's bookings look like theirs, which is worse than none.
    ///
    /// Looked up by NormalizedEmail, never by Email: the setter maintains it and the unique index is on it.
    /// </remarks>
    private async Task UpsertPrincipalAsync(Guid documentId, Guid tenantId, string? address, CancellationToken cancellationToken)
    {
        var existing = await ResourcePrincipals.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.ResourceDocumentId == documentId, cancellationToken);

        var normalized = address?.Trim().ToUpperInvariant();
        var userId = string.IsNullOrEmpty(normalized)
            ? null
            : await Users.IgnoreQueryFilters()
                .Where(u => u.TenantId == tenantId && u.NormalizedEmail == normalized)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (userId is not { } resolved)
        {
            if (existing is not null)
            {
                ResourcePrincipals.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            ResourcePrincipals.Add(new ResourcePrincipal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ResourceDocumentId = documentId,
                UserId = resolved,
            });
        }
        else if (existing.UserId != resolved)
        {
            existing.UserId = resolved;
        }
    }
}
