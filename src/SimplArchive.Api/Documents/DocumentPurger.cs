using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Permanently removes soft-deleted (recycle-bin) documents (ADR "Manual hard-delete / purge") — their
// object-storage blobs (+ derived rendition/preview/text-layout artifacts), DB rows (via FK cascades;
// LegalHoldItems removed explicitly, since that FK is Restrict), and search-index entries. Shared by
// DocumentsController (per-item purge) and RepositoriesController (empty recycle bin). Irreversible.
public sealed class DocumentPurger
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly ILegalHoldService _legalHold;
    private readonly IStorageQuotaService _storageQuota;

    public DocumentPurger(
        SimplArchiveDbContext dbContext,
        IObjectStorageClient objectStorage,
        IDocumentIndexQueue indexQueue,
        ILegalHoldService legalHold,
        IStorageQuotaService storageQuota)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _indexQueue = indexQueue;
        _legalHold = legalHold;
        _storageQuota = storageQuota;
    }

    // Collects a soft-deleted document + all of its (also soft-deleted) descendants — the set a per-item purge
    // removes. Null if the document doesn't exist; the empty subtree with root.DeletedAt == null signals "not
    // in the recycle bin" (the caller rejects that as CANNOT_PURGE_ACTIVE).
    public async Task<List<Document>?> CollectSubtreeAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var root = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .SingleOrDefaultAsync(d => d.Id == rootId, cancellationToken);
        if (root is null)
        {
            return null;
        }

        var all = new List<Document> { root };
        var level = new List<Guid> { rootId };
        while (level.Count > 0)
        {
            var children = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.ParentId != null && level.Contains(d.ParentId!.Value))
                .ToListAsync(cancellationToken);
            if (children.Count == 0)
            {
                break;
            }

            all.AddRange(children);
            level = children.Select(c => c.Id).ToList();
        }

        return all;
    }

    // True if any of the documents is directly under an active legal hold — defensive: a recycle-bin item can't
    // be held by construction (a held document can't be soft-deleted), but purge is irreversible.
    public Task<bool> AnyHeldAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        _legalHold.AnyDirectlyHeldAsync(documentIds, cancellationToken);

    // Permanently removes the given soft-deleted documents; returns each purged (id, name) so the caller can
    // audit them. The DB is authoritative — rows are committed first, then blobs are best-effort deleted (an
    // orphaned blob is harmless), then the documents are dropped from the search index.
    public async Task<IReadOnlyList<(Guid Id, string Name)>> PurgeAsync(IReadOnlyList<Document> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        var ids = documents.Select(d => d.Id).ToList();
        var purged = documents.Select(d => (d.Id, d.Name)).ToList();

        // Every version's object key (before the rows go) — to delete the blobs + their derived artifacts.
        var objectKeys = await _dbContext.DocumentVersions
            .Where(v => ids.Contains(v.DocumentId))
            .Select(v => v.ObjectKey)
            .ToListAsync(cancellationToken);

        // The storage each purged version's blob accounted for, per tenant — to decrement the tenant counter once
        // the rows are gone (ADR "Per-tenant storage quota"). Only versions with a SizeBytes were ever counted.
        var freedByTenant = await _dbContext.DocumentVersions
            .Where(v => ids.Contains(v.DocumentId) && v.SizeBytes != null)
            .GroupBy(v => v.TenantId)
            .Select(g => new { TenantId = g.Key, Freed = g.Sum(v => v.SizeBytes!.Value) })
            .ToListAsync(cancellationToken);

        // WORM: refuse the purge if any blob is still immutable — a retention lock not yet expired, or an object
        // legal hold — so storage-enforced immutability outlives the soft-delete (ADR "WORM / immutable document
        // versions"). A read failure (e.g. a non-object-lock bucket) doesn't block: WORM isn't active there.
        var now = DateTimeOffset.UtcNow;
        foreach (var key in objectKeys.Where(k => k is not null))
        {
            ObjectLockStatus status;
            try
            {
                status = await _objectStorage.GetLockStatusAsync(key!, cancellationToken);
            }
            catch (Exception)
            {
                continue;
            }

            if (status.IsLocked(now))
            {
                throw new WormLockedException();
            }
        }

        // LegalHoldItem's Document FK is Restrict (a released hold leaves its item rows behind), so remove those
        // explicitly; every other dependent (versions, field values, comments, references, ACL) cascades.
        var holdItems = await _dbContext.LegalHoldItems.Where(i => ids.Contains(i.DocumentId)).ToListAsync(cancellationToken);
        _dbContext.LegalHoldItems.RemoveRange(holdItems);
        _dbContext.Documents.RemoveRange(documents);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Release the purged blobs' storage from the tenant counter (ADR "Per-tenant storage quota").
        foreach (var tenant in freedByTenant)
        {
            await _storageQuota.AdjustUsageAsync(tenant.TenantId, -tenant.Freed, cancellationToken);
        }

        foreach (var key in objectKeys)
        {
            await DeleteObjectAndArtifactsAsync(key, cancellationToken);
        }

        // Drop each purged document from the search index (SyncAsync removes what no longer exists).
        await _indexQueue.EnqueueManyAsync(ids, cancellationToken);

        return purged;
    }

    private async Task DeleteObjectAndArtifactsAsync(string objectKey, CancellationToken cancellationToken)
    {
        // Every derived artifact (<stem>.preview.*, <stem>.textlayout.json, …) shares the object key's stem
        // (the GUID path without its extension), so listing by that prefix catches the original + all of them.
        var extension = Path.GetExtension(objectKey);
        var stem = extension.Length == 0 ? objectKey : objectKey[..^extension.Length];
        try
        {
            var objects = await _objectStorage.ListObjectsAsync(stem, cancellationToken);
            foreach (var obj in objects)
            {
                await _objectStorage.DeleteObjectAsync(obj.Key, cancellationToken);
            }
        }
        catch (Exception)
        {
            // Best-effort — the DB is authoritative; an orphaned blob is harmless.
        }
    }
}
