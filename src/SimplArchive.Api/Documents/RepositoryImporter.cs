using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Import;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// How a merge-import handles an incoming leaf document whose name matches an existing document in the target
// folder (ADR "Leaf-document merge modes"). Only applies when merge = true; folders always merge by name.
public enum LeafMergeMode
{
    Rename,      // default + backward-compatible: create the leaf with an auto-renamed name (a distinct copy)
    NewVersion,  // append the incoming leaf's versions (renumbered, hash-deduped) onto the existing document
    Skip,        // drop the incoming leaf entirely, leaving the existing document untouched
}

// Imports an archive produced by RepositoryExporter (ADR "Repository import") into the caller's tenant, either
// grafted under a chosen folder or as a new repository. Recreates the folder tree, versions (+ blobs), index
// values, comments, and references with fresh Guids; maps referenced masks (well-known merged by id, custom
// created new) and principals (users matched by email, else a deactivated placeholder). ACL/workflow/hold state
// isn't in the archive, so it isn't restored; retention rides along via the mask. No schema change.
public sealed class RepositoryImporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    private readonly ICurrentTenantAccessor _tenant;
    private readonly IWellKnownMaskSeeder _seeder;
    private readonly IStorageQuotaService _storageQuota;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly ISearchablePdfQueue _searchablePdfQueue;

    // A confirmed TIFF (always) or PDF (if it's a scan) gets an auto-generated searchable-PDF successor — the same
    // extensions DocumentFinalizer triggers on (ADR "Searchable PDF successor for TIFFs").
    private static readonly HashSet<string> SearchablePdfSourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff", ".pdf" };

    // Running total of imported blob bytes (ADR "Per-tenant storage quota") — added to the tenant counter once at
    // the end of the import.
    private long _importedBytes;

    // Imported versions eligible for a searchable-PDF successor (ADR 0527) — enqueued once at the end.
    private readonly List<SearchablePdfJob> _searchablePdfJobs = [];

    public RepositoryImporter(SimplArchiveDbContext dbContext, IObjectStorageClient objectStorage, ICurrentTenantAccessor tenant, IWellKnownMaskSeeder seeder, IStorageQuotaService storageQuota, IDocumentIndexQueue indexQueue, ISearchablePdfQueue searchablePdfQueue)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _tenant = tenant;
        _seeder = seeder;
        _storageQuota = storageQuota;
        _indexQueue = indexQueue;
        _searchablePdfQueue = searchablePdfQueue;
    }

    public sealed record ImportResult(Guid RootDocumentId, string RootName, int Documents, int Versions, int Comments, int Skipped);

    // targetFolderId == null → the archive root becomes a new repository (a root document); otherwise the root is
    // grafted as a child of that folder. updateExisting: on a re-import (a document matched by its origin key),
    // false leaves the existing document untouched (only never-imported documents/versions are added — a
    // re-import of an unchanged archive is a no-op); true syncs the matched document (name/mask/index-data +
    // new versions). See ADR "Idempotent re-import". The stream is read fully (a zip needs random access).
    public async Task<ImportResult> ImportAsync(Stream zipStream, Guid? targetFolderId, bool updateExisting, bool includePermissions, bool merge, LeafMergeMode leafMode, CancellationToken cancellationToken)
    {
        var tenantId = _tenant.TenantId ?? throw new NoTenantException();

        using var buffer = new MemoryStream();
        await zipStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var manifest = ReadJson<ArchiveManifest>(archive, "manifest.json")
            ?? throw InvalidArchiveException.MissingManifest();
        if (manifest.FormatVersion != RepositoryExporter.FormatVersion)
        {
            throw new UnsupportedArchiveVersionException(manifest.FormatVersion);
        }

        var originTenant = manifest.Source.TenantId;
        var masks = ReadJson<List<ArchiveMask>>(archive, "masks/masks.json") ?? [];
        var principals = ReadJson<ArchivePrincipals>(archive, "principals/principals.json") ?? new ArchivePrincipals([], [], [], []);
        var documents = ReadLines<ArchiveDocument>(archive, "tree/documents.jsonl");
        var versions = ReadLines<ArchiveVersion>(archive, "tree/versions.jsonl");
        var indexValues = ReadLines<ArchiveIndexValue>(archive, "tree/index-data.jsonl");
        // The chat thread lives in "tree/chat.jsonl" since issue #382 (it was "tree/comments.jsonl", with a
        // "parentCommentId" field). No compatibility shim: FormatVersion was bumped instead, so a pre-#382
        // archive is rejected loudly by the check above rather than importing with its threads silently missing.
        var comments = ReadLines<ArchiveChatMessage>(archive, "tree/chat.jsonl");
        var annotations = ReadLines<ArchiveAnnotation>(archive, "tree/annotations.jsonl");
        var references = ReadLines<ArchiveReference>(archive, "tree/references.jsonl");
        var aclEntries = includePermissions ? ReadLines<ArchiveAcl>(archive, "acl/acl.jsonl") : [];

        if (targetFolderId is { } tid && !await _dbContext.Documents.AnyAsync(d => d.Id == tid, cancellationToken))
        {
            throw new ImportTargetNotFoundException();
        }

        await _seeder.EnsureWellKnownMasksAsync(tenantId, cancellationToken);

        // Sensitivity labels (ADR "Classification in export/import"): merge the archive's label catalog into the
        // destination tenant by name — create any that are missing (with the archived rank/colour/watermark),
        // leave existing ones' config untouched — and build a name → id map that documents + mask defaults resolve
        // against. Labels are document classification metadata, so they travel regardless of the permissions toggle.
        var archiveLabels = ReadJson<List<ArchiveLabel>>(archive, "labels/labels.json") ?? [];
        var labelMap = await EnsureLabelsAsync(archiveLabels, tenantId, cancellationToken);

        // Match archive documents to already-imported ones by their origin key. A matched document is reused
        // (its target id seeds docMap); an unmatched one is created fresh. updateExisting decides whether a
        // matched document is also synced.
        var archiveDocIds = documents.Select(d => d.Id).ToList();
        var existingByOrigin = await _dbContext.Documents
            .Where(d => d.OriginTenantId == originTenant && d.OriginDocumentId != null && archiveDocIds.Contains(d.OriginDocumentId!.Value))
            .ToDictionaryAsync(d => d.OriginDocumentId!.Value, d => d.Id, cancellationToken);

        var docMap = new Dictionary<Guid, Guid>(existingByOrigin);
        var newDocs = documents.Where(d => !existingByOrigin.ContainsKey(d.Id)).ToList();
        var updatedDocs = updateExisting ? documents.Where(d => existingByOrigin.ContainsKey(d.Id)).ToList() : [];

        var rootDoc = documents.SingleOrDefault(d => d.Id == manifest.Root.DocumentId)
            ?? throw InvalidArchiveException.MissingRoot();

        // Merge planning (ADR "Merge-into-existing import"): when merging into a target folder, an archive folder
        // whose name matches an existing folder under its (resolved) parent is reused rather than created — an
        // overlay of the two trees. A leaf document, or a folder under a to-be-created parent, is always created
        // (auto-renamed on a name clash). This runs read-only before mask mapping so it knows which documents are
        // actually created. Non-merge modes create every new document (mergedIds empty).
        var archiveHasVersions = versions.Select(v => v.DocumentId).ToHashSet();
        var childrenByArchiveParent = documents.Where(d => d.ParentId is not null)
            .GroupBy(d => d.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var mergedIds = new HashSet<Guid>();
        // Leaf-conflict handling (ADR "Leaf-document merge modes"): an incoming leaf whose name matches an existing
        // document in the resolved target folder is, per leafMode, either appended-as-versions (NewVersion) or
        // dropped (Skip). Rename leaves it to the default create-with-unique-name path.
        var leafVersionMergeIds = new HashSet<Guid>(); // archive leaf id → append its versions to docMap[id]
        var skippedIds = new HashSet<Guid>();
        if (merge && targetFolderId is { } mergeTarget)
        {
            var walk = new Queue<(ArchiveDocument Doc, Guid ParentTarget)>();
            walk.Enqueue((rootDoc, mergeTarget));
            while (walk.Count > 0)
            {
                var (doc, parentTarget) = walk.Dequeue();
                Guid? resolvedTarget = null;
                if (existingByOrigin.TryGetValue(doc.Id, out var originMatch))
                {
                    resolvedTarget = originMatch; // already placed (a prior import) — descend into it
                }
                else if (!archiveHasVersions.Contains(doc.Id) && await FindFolderChildByNameAsync(parentTarget, doc.Name, cancellationToken) is { } existing)
                {
                    docMap[doc.Id] = existing;
                    mergedIds.Add(doc.Id);
                    resolvedTarget = existing;
                }
                else if (archiveHasVersions.Contains(doc.Id) && leafMode != LeafMergeMode.Rename
                    && await FindLeafChildByNameAsync(parentTarget, doc.Name, cancellationToken) is { } existingLeaf)
                {
                    if (leafMode == LeafMergeMode.NewVersion)
                    {
                        docMap[doc.Id] = existingLeaf;
                        leafVersionMergeIds.Add(doc.Id);
                    }
                    else
                    {
                        skippedIds.Add(doc.Id); // Skip: drop this leaf (and any subtree — not descended)
                    }
                }

                if (resolvedTarget is { } rt && childrenByArchiveParent.TryGetValue(doc.Id, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        walk.Enqueue((kid, rt));
                    }
                }
            }
        }

        var createdIds = newDocs.Where(d => !mergedIds.Contains(d.Id) && !leafVersionMergeIds.Contains(d.Id) && !skippedIds.Contains(d.Id)).Select(d => d.Id).ToHashSet();
        var createdDocs = newDocs.Where(d => createdIds.Contains(d.Id)).ToList();
        var touchedDocs = createdDocs.Concat(updatedDocs).ToList();

        var userMap = await MapPrincipalsAsync(principals, tenantId, includePermissions, cancellationToken);

        // Only map (and create) masks actually referenced by a created/updated document.
        var neededMaskVersionIds = touchedDocs.Where(d => d.MaskVersionId is not null).Select(d => d.MaskVersionId!.Value).ToHashSet();
        var (maskVersionMap, fieldDefMap) = await MapMasksAsync(masks.Where(m => neededMaskVersionIds.Contains(m.Version.MaskVersionId)).ToList(), tenantId, labelMap, cancellationToken);

        // Phase A: persist the placeholder principals + new custom masks (assigns MaskVersion numbering).
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Phase B: create the documents that weren't reused (origin-matched or merged) parent-before-child — each
        // round creates every one whose parent is now resolvable, commits so it can parent the next round, and
        // repeats. The root's parent is the target folder (graft/merge) or null (new repository). Sibling-name
        // clashes at the destination are auto-renamed.
        var toCreate = createdDocs.ToList();
        while (toCreate.Count > 0)
        {
            var ready = toCreate.Where(d => d.Id == rootDoc.Id || (d.ParentId is { } p && docMap.ContainsKey(p))).ToList();
            if (ready.Count == 0)
            {
                break;
            }

            foreach (var doc in ready)
            {
                var parentTargetId = doc.Id == rootDoc.Id ? targetFolderId : docMap[doc.ParentId!.Value];
                var name = await UniqueChildNameAsync(parentTargetId, doc.Name, cancellationToken);
                AddDocument(doc, docMap, tenantId, originTenant, parentTargetId, name, userMap);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            toCreate = toCreate.Except(ready).ToList();
        }

        // Existing version numbers per updated document (so a sync only adds versions it doesn't already have).
        var updatedTargetIds = updatedDocs.Select(d => docMap[d.Id]).ToList();
        var existingVersionNumbers = updatedTargetIds.Count == 0
            ? []
            : (await _dbContext.DocumentVersions.Where(v => updatedTargetIds.Contains(v.DocumentId) && v.VersionNumber != null)
                .Select(v => new { v.DocumentId, v.VersionNumber }).ToListAsync(cancellationToken))
                .GroupBy(v => v.DocumentId).ToDictionary(g => g.Key, g => g.Select(x => x.VersionNumber!.Value).ToHashSet());

        // Phase C: versions (+ blobs), index values, comments, references. versionMap records the archive→new
        // version id for each version actually created this run — so annotations attach only to created versions
        // (a no-op re-import creates no versions ⇒ no duplicated annotations).
        var versionMap = new Dictionary<Guid, Guid>();
        foreach (var version in versions.Where(v => docMap.ContainsKey(v.DocumentId)))
        {
            var targetDocId = docMap[version.DocumentId];
            var isNew = createdIds.Contains(version.DocumentId);
            var isUpdated = updateExisting && existingByOrigin.ContainsKey(version.DocumentId);
            if (!isNew && !isUpdated)
            {
                continue; // matched + skip
            }

            if (isUpdated && version.VersionNumber is { } vn && existingVersionNumbers.TryGetValue(targetDocId, out var present) && present.Contains(vn))
            {
                continue; // the update already has this version
            }

            await ImportVersionAsync(version, targetDocId, tenantId, archive, userMap, versionMap, cancellationToken);
        }

        // Leaf-merge (ADR "Leaf-document merge modes", NewVersion mode): append each incoming leaf's versions onto
        // the matched existing document — renumbered after its current highest version, in archive order, skipping
        // any whose content hash the target already has (so a re-merge doesn't duplicate).
        var appendedVersions = 0;
        foreach (var group in versions.Where(v => leafVersionMergeIds.Contains(v.DocumentId)).GroupBy(v => v.DocumentId))
        {
            var targetDocId = docMap[group.Key];
            var existing = await _dbContext.DocumentVersions
                .Where(v => v.DocumentId == targetDocId)
                .Select(v => new { v.VersionNumber, v.Sha256Hash })
                .ToListAsync(cancellationToken);
            var hashes = existing.Where(v => v.Sha256Hash != null).Select(v => v.Sha256Hash!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextNumber = existing.Where(v => v.VersionNumber != null).Select(v => v.VersionNumber!.Value).DefaultIfEmpty(0).Max() + 1;

            foreach (var version in group.OrderBy(v => v.VersionNumber ?? 0))
            {
                if (version.Sha256 is { } sha && hashes.Contains(sha))
                {
                    continue; // identical content already present
                }

                await ImportVersionAsync(version, targetDocId, tenantId, archive, userMap, versionMap, cancellationToken, versionNumberOverride: nextNumber);
                if (version.Sha256 is { } s)
                {
                    hashes.Add(s);
                }

                nextNumber++;
                appendedVersions++;
            }
        }

        // Index values: a new document gets the archive's; an updated one has its set replaced.
        var updatedTargetIdSet = updatedTargetIds.ToHashSet();
        if (updatedTargetIdSet.Count > 0)
        {
            var stale = await _dbContext.FieldValues.Where(f => updatedTargetIdSet.Contains(f.DocumentId)).ToListAsync(cancellationToken);
            _dbContext.FieldValues.RemoveRange(stale);
        }

        foreach (var value in indexValues)
        {
            if (docMap.TryGetValue(value.DocumentId, out var newDocId)
                && (createdIds.Contains(value.DocumentId) || updatedTargetIdSet.Contains(newDocId))
                && fieldDefMap.TryGetValue(value.FieldDefinitionId, out var newFieldId))
            {
                _dbContext.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = newDocId, FieldDefinitionId = newFieldId, Value = value.Value });
            }
        }

        // Annotations (notes + markup shapes, ADR "Annotations in export/import") — attach to every version
        // actually created this run (versionMap), so they ride onto new documents AND newly-added versions on
        // updated documents, without duplicating on a no-op re-import (which creates no versions).
        foreach (var annotation in annotations.Where(a => versionMap.ContainsKey(a.DocumentVersionId) && docMap.ContainsKey(a.DocumentId)))
        {
            var (userId, svcId) = ResolveCreator(annotation.CreatedByUserId, annotation.CreatedByServiceAccountId, userMap);
            _dbContext.DocumentAnnotations.Add(new DocumentAnnotation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = docMap[annotation.DocumentId],
                DocumentVersionId = versionMap[annotation.DocumentVersionId],
                PageIndex = annotation.PageIndex,
                Kind = (AnnotationKind)annotation.Kind,
                PositionX = annotation.PositionX,
                PositionY = annotation.PositionY,
                Width = annotation.Width,
                Height = annotation.Height,
                Points = annotation.Points,
                Text = annotation.Text,
                Color = annotation.Color,
                CreatedByUserId = userId,
                CreatedByServiceAccountId = svcId,
                CreatedAt = annotation.CreatedAt,
                UpdatedAt = annotation.CreatedAt,
            });
        }

        // Chat messages + references are recreated only for brand-new documents (they carry no origin key, so
        // re-importing them onto a matched document would duplicate them).
        var messageMap = new Dictionary<Guid, Guid>();
        foreach (var message in comments.Where(c => c.ParentMessageId is null && createdIds.Contains(c.DocumentId)))
        {
            AddChatMessage(message, messageMap, docMap, tenantId, null, userMap);
        }

        foreach (var message in comments.Where(c => c.ParentMessageId is not null && createdIds.Contains(c.DocumentId) && messageMap.ContainsKey(c.ParentMessageId!.Value)))
        {
            AddChatMessage(message, messageMap, docMap, tenantId, messageMap[message.ParentMessageId!.Value], userMap);
        }

        foreach (var reference in references.Where(r => createdIds.Contains(r.ParentFolderId) && docMap.ContainsKey(r.TargetDocumentId)))
        {
            var (userId, svcId) = ResolveCreator(reference.CreatedByUserId, reference.CreatedByServiceAccountId, userMap);
            _dbContext.DocumentReferences.Add(new DocumentReference
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentFolderId = docMap[reference.ParentFolderId],
                TargetDocumentId = docMap[reference.TargetDocumentId],
                CreatedByUserId = userId,
                CreatedByServiceAccountId = svcId,
                CreatedAt = reference.CreatedAt,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Phase D: apply name/mask to created + updated documents now that index values exist (the required-field
        // trigger, ADR 0176, validates against them).
        foreach (var doc in touchedDocs)
        {
            var entity = await _dbContext.Documents.SingleAsync(d => d.Id == docMap[doc.Id], cancellationToken);
            if (updateExisting && existingByOrigin.ContainsKey(doc.Id))
            {
                // Sync a matched document to the archive: take the archive name unless a different sibling already
                // holds it (then keep the current name rather than fail on the uniqueness check), and refresh the
                // inheritance flag.
                var clash = await _dbContext.Documents.AnyAsync(d => d.ParentId == entity.ParentId && d.Id != entity.Id && d.Name == doc.Name, cancellationToken);
                if (!clash)
                {
                    entity.Name = doc.Name;
                }

                entity.BreaksInheritance = doc.BreaksInheritance;
            }

            entity.MaskVersionId = doc.MaskVersionId is { } mv && maskVersionMap.TryGetValue(mv, out var mapped) ? mapped : null;
            // The document's sensitivity label (ADR "Classification in export/import") resolves by name against the
            // merged catalog; null (unlabelled) or an unknown label clears it.
            entity.SensitivityLabelId = doc.SensitivityLabel is { } sl && labelMap.TryGetValue(sl, out var labelId) ? labelId : null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Phase E: ACL grants (opt-in, ADR "ACL in export/import"). New documents get the archived grants; an
        // updated document has its grants replaced; a matched-but-skipped one is left untouched. Groups are
        // matched by name (a placeholder is created if absent); users/service-accounts reuse the principal map.
        if (includePermissions && aclEntries.Count > 0)
        {
            var groupMap = await MapGroupsAsync(principals.Groups, tenantId, cancellationToken);
            var updatedTargets = updatedDocs.Select(d => docMap[d.Id]).ToHashSet();
            if (updatedTargets.Count > 0)
            {
                _dbContext.AclEntries.RemoveRange(await _dbContext.AclEntries.Where(a => updatedTargets.Contains(a.DocumentId)).ToListAsync(cancellationToken));
            }

            foreach (var acl in aclEntries)
            {
                if (!docMap.TryGetValue(acl.DocumentId, out var targetDocId)
                    || !(createdIds.Contains(acl.DocumentId) || (updateExisting && existingByOrigin.ContainsKey(acl.DocumentId))))
                {
                    continue;
                }

                var userId = acl.UserId is { } u && userMap.TryGetValue(u, out var ur) ? ur.UserId : null;
                var svcId = acl.ServiceAccountId is { } s && userMap.TryGetValue(s, out var sr) ? sr.ServiceAccountId : null;
                var groupId = acl.GroupId is { } g && groupMap.TryGetValue(g, out var gid) ? gid : (Guid?)null;
                if (userId is null && svcId is null && groupId is null)
                {
                    continue; // principal didn't resolve — skip rather than write an orphan grant
                }

                _dbContext.AclEntries.Add(new AclEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DocumentId = targetDocId,
                    UserId = userId,
                    GroupId = groupId,
                    ServiceAccountId = svcId,
                    CanSee = acl.CanSee,
                    CanReadContent = acl.CanReadContent,
                    CanEditContent = acl.CanEditContent,
                    CanEditIndexData = acl.CanEditIndexData,
                    CanDelete = acl.CanDelete,
                    CanCreateSubItems = acl.CanCreateSubItems,
                    CanMove = acl.CanMove,
                    CanManagePermissions = acl.CanManagePermissions,
                    CanAnnotate = acl.CanAnnotate,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            // Group memberships (ADR "Group memberships in export") — additively add each archived edge (group +
            // user resolved through the maps) that isn't already present; never remove an existing member.
            foreach (var m in principals.Memberships)
            {
                if (groupMap.TryGetValue(m.GroupId, out var gid)
                    && userMap.TryGetValue(m.UserId, out var ur) && ur.UserId is { } uid
                    && !await _dbContext.GroupMemberships.AnyAsync(x => x.GroupId == gid && x.UserId == uid, cancellationToken))
                {
                    _dbContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = gid, UserId = uid });
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Add the imported blobs' bytes to the tenant's used-storage counter (ADR "Per-tenant storage quota").
        await _storageQuota.AdjustUsageAsync(tenantId, _importedBytes, cancellationToken);

        // Enqueue every created/updated document for search indexing — without this the imported documents exist in
        // the database but are invisible to full-text search (ADR 0526 reindex path; the same enqueue the normal
        // create/upload paths use).
        await _indexQueue.EnqueueManyAsync(touchedDocs.Select(d => docMap[d.Id]).ToList(), cancellationToken);

        // Trigger the TIFF/PDF → searchable-PDF (OCR) conversion for the imported versions (ADR 0527) — the same
        // successor an uploaded TIFF/PDF gets; the worker does the OCR off the request path.
        await _searchablePdfQueue.EnqueueManyAsync(_searchablePdfJobs, cancellationToken);

        return new ImportResult(docMap[rootDoc.Id], rootDoc.Name, docMap.Count, versions.Count(v => createdIds.Contains(v.DocumentId)), messageMap.Count, existingByOrigin.Count);
    }

    // Matches each archived group by name, creating a deactivated (empty) placeholder if absent — so an ACL grant
    // to a group survives the import even when the target tenant doesn't have that group yet. Memberships are a
    // tenant concern and aren't imported (ADR "ACL in export/import").
    private async Task<Dictionary<Guid, Guid>> MapGroupsAsync(List<ArchiveGroup> groups, Guid tenantId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var group in groups)
        {
            var existing = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Name == group.Name && g.ParentGroupId == null, cancellationToken);
            if (existing is null)
            {
                existing = new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = group.Name, CreatedAt = DateTimeOffset.UtcNow };
                _dbContext.Groups.Add(existing);
            }

            // Clearance travels with permissions, applied max-never-lower (ADR "Classification in export/import").
            if (group.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[group.Id] = existing.Id;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return map;
    }

    // Ensures each archived sensitivity label exists in the destination tenant (created by name if absent, with
    // the archived rank/colour/watermark; an existing label's config is left untouched), returning a name → id map
    // — see ADR "Classification in export/import". Committed here so documents + mask defaults can reference the ids.
    private async Task<Dictionary<string, Guid>> EnsureLabelsAsync(List<ArchiveLabel> labels, Guid tenantId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (labels.Count == 0)
        {
            return map;
        }

        var existing = await _dbContext.SensitivityLabelDefinitions.ToDictionaryAsync(l => l.Name, l => l, cancellationToken);
        foreach (var label in labels)
        {
            if (!existing.TryGetValue(label.Name, out var entity))
            {
                entity = new SimplArchive.Domain.Documents.SensitivityLabelDefinition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Name = label.Name,
                    Rank = label.Rank,
                    Color = label.Color,
                    Watermark = label.Watermark,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _dbContext.SensitivityLabelDefinitions.Add(entity);
                existing[label.Name] = entity;
            }

            map[label.Name] = entity.Id;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return map;
    }

    private void AddDocument(ArchiveDocument doc, Dictionary<Guid, Guid> docMap, Guid tenantId, Guid originTenant, Guid? parentId, string name, IReadOnlyDictionary<Guid, PrincipalRef> userMap)
    {
        var (userId, svcId) = ResolveCreator(doc.CreatedByUserId, doc.CreatedByServiceAccountId, userMap);
        var newId = Guid.NewGuid();
        docMap[doc.Id] = newId;
        _dbContext.Documents.Add(new Document
        {
            Id = newId,
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = null, // set in phase D
            CreatedByUserId = userId,
            CreatedByServiceAccountId = svcId,
            CreatedAt = doc.CreatedAt,
            BreaksInheritance = doc.BreaksInheritance,
            OriginTenantId = originTenant,
            OriginDocumentId = doc.Id,
        });
    }

    private async Task ImportVersionAsync(ArchiveVersion version, Guid newDocId, Guid tenantId, ZipArchive archive, IReadOnlyDictionary<Guid, PrincipalRef> userMap, Dictionary<Guid, Guid> versionMap, CancellationToken cancellationToken, int? versionNumberOverride = null)
    {
        if (version.BlobRef is not { } blobRef || archive.GetEntry($"blobs/{blobRef}") is not { } entry)
        {
            throw InvalidArchiveException.MissingBlob();
        }

        byte[] bytes;
        await using (var open = entry.Open())
        using (var ms = new MemoryStream())
        {
            await open.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        var computed = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(computed, blobRef, StringComparison.OrdinalIgnoreCase) && !string.Equals(computed, version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveBlobCorruptException();
        }

        // The key groups by the imported document's storage folder (ADR 0530), bucketed by the VERSION's filing
        // year (ADR 0520). The StorageFolderId comes from the document — freshly added earlier in this import
        // (tracked in Local) OR an existing document we're appending a version onto (NewVersion leaf-merge, ADR
        // "Leaf-document merge modes"), which isn't in the change tracker — so read it from whichever applies.
        var newVersionId = Guid.NewGuid();
        var storageFolderId = _dbContext.Documents.Local.FirstOrDefault(d => d.Id == newDocId)?.StorageFolderId
            ?? await _dbContext.Documents.Where(d => d.Id == newDocId).Select(d => d.StorageFolderId).FirstAsync(cancellationToken);
        var objectKey = ObjectKeyBuilder.Build(tenantId, version.FiledAt, storageFolderId, newVersionId, version.FileExtension);
        await _objectStorage.PutObjectAsync(objectKey, new MemoryStream(bytes), "application/octet-stream", cancellationToken);

        var (userId, svcId) = ResolveCreator(version.CreatedByUserId, version.CreatedByServiceAccountId, userMap);
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = newVersionId,
            TenantId = tenantId,
            DocumentId = newDocId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = versionNumberOverride ?? version.VersionNumber,
            Sha256Hash = version.Sha256,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = svcId,
            CreatedAt = version.FiledAt,
            DocumentDate = DateOnly.ParseExact(version.DocumentDate, "yyyy-MM-dd"),
            OcrLanguages = version.OcrLanguages,
            Comment = version.Comment,
            SizeBytes = bytes.Length, // storage-quota accounting (ADR "Per-tenant storage quota")
        });
        versionMap[version.Id] = newVersionId; // for annotations (ADR "Annotations in export/import")
        _importedBytes += bytes.Length;

        // A TIFF/PDF version gets a searchable-PDF (OCR) successor, like an uploaded one (ADR 0527) — enqueued at end.
        if (SearchablePdfSourceExtensions.Contains(Path.GetExtension(objectKey)))
        {
            _searchablePdfJobs.Add(new SearchablePdfJob(tenantId, newDocId, newVersionId));
        }
    }

    private void AddChatMessage(ArchiveChatMessage message, Dictionary<Guid, Guid> messageMap, IReadOnlyDictionary<Guid, Guid> docMap, Guid tenantId, Guid? parentMessageId, IReadOnlyDictionary<Guid, PrincipalRef> userMap)
    {
        var (userId, svcId) = ResolveCreator(message.CreatedByUserId, message.CreatedByServiceAccountId, userMap);
        var newId = Guid.NewGuid();
        messageMap[message.Id] = newId;
        _dbContext.ChatMessages.Add(new ChatMessage
        {
            Id = newId,
            TenantId = tenantId,
            DocumentId = docMap[message.DocumentId],
            ParentMessageId = parentMessageId,
            Body = message.Body,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = svcId,
            CreatedAt = message.CreatedAt,
        });
    }

    // Matches each archived user by email (else a deactivated placeholder) and each service account by name (else
    // a deactivated placeholder with an inert client id). Returns archive-principal-id → target-id, keyed for
    // both principal kinds (ids are Guids, so one map is unambiguous).
    private async Task<Dictionary<Guid, PrincipalRef>> MapPrincipalsAsync(ArchivePrincipals principals, Guid tenantId, bool includePermissions, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, PrincipalRef>();

        foreach (var user in principals.Users)
        {
            var normalized = user.Email.ToUpperInvariant();
            var existing = await _dbContext.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
            if (existing is null)
            {
                existing = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = user.Email, DisplayName = user.DisplayName, IsActive = false, CreatedAt = DateTimeOffset.UtcNow };
                _dbContext.Users.Add(existing);
            }

            // Clearance travels with permissions, applied max-never-lower (ADR "Classification in export/import").
            if (includePermissions && user.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[user.Id] = new PrincipalRef(existing.Id, null);
        }

        foreach (var svc in principals.ServiceAccounts)
        {
            var existing = await _dbContext.ServiceAccounts.FirstOrDefaultAsync(s => s.Name == svc.Name, cancellationToken);
            if (existing is null)
            {
                existing = new ServiceAccount { Id = Guid.NewGuid(), TenantId = tenantId, Name = svc.Name, OpenIddictApplicationClientId = $"imported:{Guid.NewGuid():N}", IsActive = false, CreatedAt = DateTimeOffset.UtcNow };
                _dbContext.ServiceAccounts.Add(existing);
            }

            if (includePermissions && svc.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[svc.Id] = new PrincipalRef(null, existing.Id);
        }

        return map;
    }

    // Well-known masks merge into the target's current version (fields matched by name); custom masks are created
    // fresh. Returns archive-MaskVersionId → target-MaskVersionId and archive-FieldDefinitionId → target-FieldDefinitionId.
    private async Task<(Dictionary<Guid, Guid> MaskVersions, Dictionary<Guid, Guid> Fields)> MapMasksAsync(List<ArchiveMask> masks, Guid tenantId, IReadOnlyDictionary<string, Guid> labelMap, CancellationToken cancellationToken)
    {
        var maskVersionMap = new Dictionary<Guid, Guid>();
        var fieldMap = new Dictionary<Guid, Guid>();
        var takenNames = await _dbContext.MaskVersions.Where(m => m.IsCurrent).Select(m => m.Name).ToListAsync(cancellationToken);

        foreach (var mask in masks)
        {
            if (mask.WellKnown)
            {
                var current = await _dbContext.MaskVersions.FirstOrDefaultAsync(m => m.MaskId == mask.MaskId && m.IsCurrent, cancellationToken);
                if (current is null)
                {
                    continue; // the well-known mask isn't present (shouldn't happen after seeding) — drop the mapping
                }

                maskVersionMap[mask.Version.MaskVersionId] = current.Id;
                var targetFields = await _dbContext.FieldDefinitions.Where(f => f.MaskVersionId == current.Id).ToListAsync(cancellationToken);
                foreach (var field in mask.Fields)
                {
                    if (targetFields.FirstOrDefault(t => t.Name == field.Name) is { } match)
                    {
                        fieldMap[field.FieldDefinitionId] = match.Id;
                    }
                }

                continue;
            }

            var newMask = new Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
            var name = UniqueName(mask.Version.Name, takenNames);
            takenNames.Add(name);
            // A custom mask's default sensitivity label (ADR "Classification in export/import") resolves by name;
            // a well-known mask (merged above) keeps the destination's own default rather than being overwritten.
            var defaultLabelId = mask.Version.DefaultSensitivityLabel is { } dl && labelMap.TryGetValue(dl, out var lid) ? lid : (Guid?)null;
            var newVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = newMask.Id, Name = name, ReviewSlaDays = mask.Version.ReviewSlaDays, RetentionYears = mask.Version.RetentionYears, DefaultSensitivityLabelId = defaultLabelId, CreatedAt = DateTimeOffset.UtcNow };
            _dbContext.Masks.Add(newMask);
            _dbContext.MaskVersions.Add(newVersion);
            maskVersionMap[mask.Version.MaskVersionId] = newVersion.Id;

            foreach (var field in mask.Fields)
            {
                var newField = new FieldDefinition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MaskVersionId = newVersion.Id,
                    Name = field.Name,
                    DataType = (FieldDataType)field.DataType,
                    IsRequired = field.IsRequired,
                    FormatPattern = field.FormatPattern,
                    MaxTextLength = field.MaxTextLength,
                    MinValue = field.MinValue,
                    MaxValue = field.MaxValue,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _dbContext.FieldDefinitions.Add(newField);
                fieldMap[field.FieldDefinitionId] = newField.Id;
            }
        }

        return (maskVersionMap, fieldMap);
    }

    private (Guid? UserId, Guid? ServiceAccountId) ResolveCreator(Guid? archiveUserId, Guid? archiveServiceAccountId, IReadOnlyDictionary<Guid, PrincipalRef> userMap)
    {
        if (archiveUserId is { } u && userMap.TryGetValue(u, out var byUser))
        {
            return (byUser.UserId, byUser.ServiceAccountId);
        }

        if (archiveServiceAccountId is { } s && userMap.TryGetValue(s, out var bySvc))
        {
            return (bySvc.UserId, bySvc.ServiceAccountId);
        }

        // A creator that didn't map (e.g. a principal absent from the archive) is attributed to the importer.
        return (_importerUserId, null);
    }

    private Guid? _importerUserId;

    // The importing admin — the fallback author for any archived creator that isn't in the archive's principals.
    public void SetImporter(Guid? userId) => _importerUserId = userId;

    private async Task<string> UniqueChildNameAsync(Guid? parentId, string desired, CancellationToken cancellationToken)
    {
        var siblings = await _dbContext.Documents.Where(d => d.ParentId == parentId).Select(d => d.Name).ToListAsync(cancellationToken);
        return UniqueName(desired, siblings);
    }

    // The id of an existing *folder* (a document with no versions) named `name` directly under `parentId`, for the
    // merge overlay (ADR "Merge-into-existing import"); null if there's none. A same-named leaf document doesn't
    // match — the incoming folder is created (auto-renamed) rather than merged into a non-folder.
    private async Task<Guid?> FindFolderChildByNameAsync(Guid parentId, string name, CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.Documents.Where(d => d.ParentId == parentId && d.Name == name).Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var id in candidates)
        {
            if (!await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == id, cancellationToken))
            {
                return id;
            }
        }

        return null;
    }

    // A same-named existing leaf (a document that has versions) under parentId — the merge-conflict target for
    // NewVersion/Skip (ADR "Leaf-document merge modes"). Null if none (or only a same-named folder exists).
    private async Task<Guid?> FindLeafChildByNameAsync(Guid parentId, string name, CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.Documents.Where(d => d.ParentId == parentId && d.Name == name).Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var id in candidates)
        {
            if (await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == id, cancellationToken))
            {
                return id;
            }
        }

        return null;
    }

    private static string UniqueName(string desired, ICollection<string> taken)
    {
        if (!taken.Contains(desired))
        {
            return desired;
        }

        var candidate = $"{desired} (imported)";
        var n = 2;
        while (taken.Contains(candidate))
        {
            candidate = $"{desired} (imported {n++})";
        }

        return candidate;
    }

    private static T? ReadJson<T>(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name) is not { } entry)
        {
            return default;
        }

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    private static List<T> ReadLines<T>(ZipArchive archive, string name)
    {
        var result = new List<T>();
        if (archive.GetEntry(name) is not { } entry)
        {
            return result;
        }

        using var reader = new StreamReader(entry.Open());
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<T>(line, Json) is { } row)
            {
                result.Add(row);
            }
        }

        return result;
    }

    private readonly record struct PrincipalRef(Guid? UserId, Guid? ServiceAccountId);

    private sealed record ArchiveManifest(int FormatVersion, ManifestSource Source, ManifestRoot Root);
    private sealed record ManifestSource(Guid TenantId, string TenantName);
    private sealed record ManifestRoot(Guid DocumentId, string Name);
    private sealed record ArchiveMask(Guid MaskId, bool WellKnown, ArchiveMaskVersion Version, List<ArchiveField> Fields);
    private sealed record ArchiveMaskVersion(Guid MaskVersionId, string Name, int VersionNumber, int? ReviewSlaDays, int? RetentionYears, string? DefaultSensitivityLabel);
    private sealed record ArchiveField(Guid FieldDefinitionId, string Name, int DataType, bool IsRequired, string? FormatPattern, int? MaxTextLength, string? MinValue, string? MaxValue);
    private sealed record ArchivePrincipals(List<ArchiveUser> Users, List<ArchiveServiceAccount> ServiceAccounts, List<ArchiveGroup> Groups, List<ArchiveMembership>? Memberships)
    {
        public List<ArchiveMembership> Memberships { get; init; } = Memberships ?? [];
    }
    private sealed record ArchiveUser(Guid Id, string Email, string DisplayName, bool IsActive, int? ClearanceRank);
    private sealed record ArchiveServiceAccount(Guid Id, string Name, bool IsActive, int? ClearanceRank);
    private sealed record ArchiveGroup(Guid Id, string Name, int? ClearanceRank);
    private sealed record ArchiveLabel(string Name, int Rank, string? Color, bool Watermark);
    private sealed record ArchiveMembership(Guid GroupId, Guid UserId);
    private sealed record ArchiveAcl(Guid DocumentId, Guid? UserId, Guid? GroupId, Guid? ServiceAccountId, bool CanSee, bool CanReadContent, bool CanEditContent, bool CanEditIndexData, bool CanDelete, bool CanCreateSubItems, bool CanMove, bool CanManagePermissions, bool CanAnnotate);
    private sealed record ArchiveDocument(Guid Id, Guid? ParentId, string Name, Guid? MaskVersionId, string? SensitivityLabel, Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, DateTimeOffset CreatedAt, bool BreaksInheritance);
    private sealed record ArchiveVersion(Guid Id, Guid DocumentId, int? VersionNumber, string DocumentDate, DateTimeOffset FiledAt, Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, string? Sha256, string? FileExtension, string? OcrLanguages, string? Comment, string? BlobRef);
    private sealed record ArchiveChatMessage(Guid Id, Guid DocumentId, Guid? ParentMessageId, string Body, Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, DateTimeOffset CreatedAt);


    private sealed record ArchiveAnnotation(Guid Id, Guid DocumentId, Guid DocumentVersionId, int PageIndex, int Kind, double PositionX, double PositionY, double? Width, double? Height, string? Points, string Text, string Color, Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, DateTimeOffset CreatedAt);
    private sealed record ArchiveReference(Guid Id, Guid ParentFolderId, Guid TargetDocumentId, Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, DateTimeOffset CreatedAt);
    private sealed record ArchiveIndexValue(Guid DocumentId, Guid FieldDefinitionId, string Value);
}
