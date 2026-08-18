using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Packages a repository (root document) or any sub-folder into a self-contained .zip an import can consume
// (ADR "Repository export"). Runs in the caller's request scope, so the tenant + soft-delete query filters
// apply automatically — only active documents of the caller's tenant are read. The archive carries the folder
// tree, document/version metadata + deduplicated blobs, per-document index-field values, the referenced mask
// definitions (all referenced MaskVersions), comments, in-scope references, and the referenced principals
// (users/service-accounts, identity only). ACLs, workflow state, legal holds, and check-out locks are
// deliberately dropped (retention rides along via the mask's RetentionYears). Read-only; no schema change.
public sealed class RepositoryExporter
{
    // 2 since issue #382: the chat thread moved from "tree/comments.jsonl" to "tree/chat.jsonl" (and its
    // "parentCommentId" field to "parentMessageId"). Bumped rather than shimmed so a v1 archive fails loudly with
    // UnsupportedArchiveVersionException instead of importing with every thread silently missing.
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;

    public RepositoryExporter(SimplArchiveDbContext dbContext, IObjectStorageClient objectStorage)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
    }

    // Writes the export archive to the supplied stream. The root must be an existing, active document of the
    // caller's tenant (validated by the caller). The stream is written but not disposed/closed by this method.
    public async Task ExportAsync(Guid rootId, RepositoryExportFilters filters, bool includePermissions, Stream output, CancellationToken cancellationToken)
    {
        // 1. Collect the active subtree (tenant + soft-delete filters apply automatically).
        var documents = await CollectSubtreeAsync(rootId, cancellationToken);
        var docIds = documents.Select(d => d.Id).ToHashSet();

        // 2. Confirmed versions of those documents + their workflow-gating state (for ActiveOnly resolution).
        var versions = await _dbContext.DocumentVersions
            .Where(v => docIds.Contains(v.DocumentId) && v.Status == DocumentVersionStatus.Confirmed)
            .ToListAsync(cancellationToken);
        var versionIds = versions.Select(v => v.Id).ToList();
        var gatedVersionIds = await _dbContext.WorkflowStates
            .Where(w => versionIds.Contains(w.DocumentVersionId) && w.Status != WorkflowStatus.Released)
            .Select(w => w.DocumentVersionId)
            .ToListAsync(cancellationToken);
        var gated = gatedVersionIds.ToHashSet();

        // 3. Principal name lookups (for the CreatedBy filter + the principals manifest). Tenant-bounded.
        var userById = await _dbContext.Users
            .Select(u => new UserLite(u.Id, u.Email, u.DisplayName, u.IsActive, u.ClearanceRank))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var serviceAccountById = await _dbContext.ServiceAccounts
            .Select(s => new ServiceAccountLite(s.Id, s.Name, s.IsActive, s.ClearanceRank))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        // 4. Resolve the per-document version set (version selection + date/createdBy filters).
        var versionsByDoc = versions.GroupBy(v => v.DocumentId).ToDictionary(g => g.Key, g => g.ToList());
        var exportedVersions = new List<DocumentVersion>();
        foreach (var document in documents)
        {
            if (!versionsByDoc.TryGetValue(document.Id, out var docVersions))
            {
                continue;
            }

            IEnumerable<DocumentVersion> candidates = docVersions;
            if (filters.Versions == ExportVersionSelection.ActiveOnly)
            {
                // The version an end user sees as current: the highest-numbered confirmed version that isn't
                // gated in a workflow (ADR "Workflow status-gating"). May not be the newest version.
                var active = docVersions
                    .Where(v => !gated.Contains(v.Id))
                    .OrderByDescending(v => v.VersionNumber ?? 0)
                    .FirstOrDefault();
                candidates = active is null ? [] : [active];
            }

            exportedVersions.AddRange(candidates.Where(v => MatchesFilters(v, filters, userById, serviceAccountById)));
        }

        // 5. Included documents = those with a surviving version, plus every ancestor up to the root (keep the
        // path intact), plus the root itself (the export anchor).
        var documentById = documents.ToDictionary(d => d.Id);
        var included = new HashSet<Guid> { rootId };
        foreach (var docId in exportedVersions.Select(v => v.DocumentId).Distinct())
        {
            for (var cursor = (Guid?)docId; cursor is { } id && included.Add(id); cursor = documentById.GetValueOrDefault(id)?.ParentId)
            {
            }
        }

        var includedDocuments = documents.Where(d => included.Contains(d.Id)).ToList();
        exportedVersions = exportedVersions.Where(v => included.Contains(v.DocumentId)).ToList();
        var exportedVersionDocIds = exportedVersions.Select(v => v.DocumentId).ToHashSet();

        // 6. Index-field values, comments, and in-scope references for the included documents.
        var fieldValues = await _dbContext.FieldValues
            .Where(f => exportedVersionDocIds.Contains(f.DocumentId))
            .ToListAsync(cancellationToken);
        // Only what a PERSON typed. An automatic entry (ADR 0545) stores no text — its wording is a localized
        // template rendered from Kind + DocumentVersionId, neither of which crosses the archive — so exporting one
        // would write an empty row that imports as a blank message nobody wrote. The importing side records its
        // own entries for the versions it creates anyway, which is where those events belong.
        //
        // Ordered client-side — SQLite can't translate a DateTimeOffset ORDER BY (the export runs against both
        // providers), and the thread is small.
        var comments = (await _dbContext.ChatMessages
            .Where(c => included.Contains(c.DocumentId) && c.Kind == ChatMessageKind.UserPost)
            .ToListAsync(cancellationToken))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToList();
        var references = await _dbContext.DocumentReferences
            .Where(r => included.Contains(r.ParentFolderId) && included.Contains(r.TargetDocumentId))
            .ToListAsync(cancellationToken);
        // Annotations (notes + markup shapes, ADR "Annotations in export/import") — anchored to an exported version.
        var exportedVersionIds = exportedVersions.Select(v => v.Id).ToHashSet();
        var annotations = await _dbContext.DocumentAnnotations
            .Where(a => exportedVersionIds.Contains(a.DocumentVersionId))
            .ToListAsync(cancellationToken);

        // 7. Referenced mask definitions — every distinct MaskVersion an included document pins.
        var maskVersionIds = includedDocuments.Where(d => d.MaskVersionId is not null).Select(d => d.MaskVersionId!.Value).Distinct().ToList();
        var maskVersions = await _dbContext.MaskVersions.Where(m => maskVersionIds.Contains(m.Id)).ToListAsync(cancellationToken);
        var maskIds = maskVersions.Select(m => m.MaskId).Distinct().ToList();
        var masks = await _dbContext.Masks.Where(m => maskIds.Contains(m.Id)).ToListAsync(cancellationToken);
        var fieldDefinitions = await _dbContext.FieldDefinitions.Where(f => maskVersionIds.Contains(f.MaskVersionId)).ToListAsync(cancellationToken);
        var fieldsByMaskVersion = fieldDefinitions.GroupBy(f => f.MaskVersionId).ToDictionary(g => g.Key, g => g.ToList());

        // 8. Referenced principals — the union of every CreatedBy across the exported documents/versions/comments.
        var referencedUserIds = new HashSet<Guid>();
        var referencedServiceAccountIds = new HashSet<Guid>();
        void Note(Guid? userId, Guid? serviceAccountId)
        {
            if (userId is { } u) referencedUserIds.Add(u);
            if (serviceAccountId is { } s) referencedServiceAccountIds.Add(s);
        }

        foreach (var d in includedDocuments) Note(d.CreatedByUserId, d.CreatedByServiceAccountId);
        foreach (var v in exportedVersions) Note(v.CreatedByUserId, v.CreatedByServiceAccountId);
        foreach (var c in comments) Note(c.CreatedByUserId, c.CreatedByServiceAccountId);

        // A MENTIONED user counts as referenced even if they never wrote anything here (issue #383) — without
        // this, someone who was only ever addressed would be missing from the principals manifest, so the import
        // could not map them and every mention of them would arrive as the unknown-user tombstone.
        foreach (var c in comments)
        {
            foreach (var mentionedId in ChatMentions.Parse(c.Body)) Note(mentionedId, null);
        }

        foreach (var a in annotations) Note(a.CreatedByUserId, a.CreatedByServiceAccountId);

        // 8b. ACL — the own AclEntry rows on the included documents (opt-in, ADR "ACL in export/import"). Their
        // principals (incl. groups, otherwise never exported) join the principals manifest.
        var aclEntries = includePermissions
            ? await _dbContext.AclEntries.Where(a => included.Contains(a.DocumentId)).ToListAsync(cancellationToken)
            : [];
        var referencedGroupIds = aclEntries.Where(a => a.GroupId is not null).Select(a => a.GroupId!.Value).ToHashSet();
        foreach (var a in aclEntries) Note(a.UserId, a.ServiceAccountId);
        var groupById = referencedGroupIds.Count == 0
            ? new Dictionary<Guid, GroupLite>()
            : await _dbContext.Groups.Where(g => referencedGroupIds.Contains(g.Id))
                .Select(g => new GroupLite(g.Id, g.Name, g.ClearanceRank))
                .ToDictionaryAsync(g => g.Id, cancellationToken);

        // Sensitivity labels (ADR "Classification in export/import"): the definitions referenced by an included
        // document's assignment or a mask version's default. Carried by name (the natural key), so import merges
        // them into the destination tenant's catalog. Always exported — a label is document classification
        // metadata, not a permission.
        var referencedLabelIds = includedDocuments.Where(d => d.SensitivityLabelId is not null).Select(d => d.SensitivityLabelId!.Value)
            .Concat(maskVersions.Where(m => m.DefaultSensitivityLabelId is not null).Select(m => m.DefaultSensitivityLabelId!.Value))
            .ToHashSet();
        var labelsById = referencedLabelIds.Count == 0
            ? new Dictionary<Guid, SimplArchive.Domain.Documents.SensitivityLabelDefinition>()
            : await _dbContext.SensitivityLabelDefinitions.Where(l => referencedLabelIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, cancellationToken);
        string? LabelName(Guid? id) => id is { } v && labelsById.TryGetValue(v, out var l) ? l.Name : null;

        // 8c. Group memberships — only the edges among users already exported (bounded; no extra users are pulled
        // in — ADR "Group memberships in export"). referencedUserIds is final at this point.
        var memberships = referencedGroupIds.Count == 0
            ? []
            : await _dbContext.GroupMemberships
                .Where(m => referencedGroupIds.Contains(m.GroupId) && referencedUserIds.Contains(m.UserId))
                .Select(m => new { m.GroupId, m.UserId })
                .ToListAsync(cancellationToken);

        var root = documentById[rootId];
        var tenant = await _dbContext.Tenants.SingleAsync(t => t.Id == root.TenantId, cancellationToken);

        // 9. Write the archive.
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // Sensitivity-label catalog (referenced definitions, matched by name on import).
        await WriteJsonAsync(archive, "labels/labels.json", labelsById.Values
            .OrderBy(l => l.Rank).ThenBy(l => l.Name)
            .Select(l => new { name = l.Name, rank = l.Rank, color = l.Color, watermark = l.Watermark }), cancellationToken);

        await WriteJsonAsync(archive, "masks/masks.json", maskVersions.Select(mv => new
        {
            maskId = mv.MaskId,
            wellKnown = IsWellKnown(mv.MaskId),
            version = new { maskVersionId = mv.Id, name = mv.Name, versionNumber = mv.VersionNumber, reviewSlaDays = mv.ReviewSlaDays, retentionYears = mv.RetentionYears, defaultSensitivityLabel = LabelName(mv.DefaultSensitivityLabelId) },
            fields = fieldsByMaskVersion.GetValueOrDefault(mv.Id, []).Select(f => new
            {
                fieldDefinitionId = f.Id,
                name = f.Name,
                dataType = (int)f.DataType,
                isRequired = f.IsRequired,
                formatPattern = f.FormatPattern,
                maxTextLength = f.MaxTextLength,
                minValue = f.MinValue,
                maxValue = f.MaxValue,
            }),
        }), cancellationToken);

        await WriteJsonAsync(archive, "principals/principals.json", new
        {
            // clearanceRank travels only with permissions (ADR "Classification in export/import") — it's an
            // access-control attribute; applied max-never-lower on import. Serialized as null when not included so
            // the importer can tell "not carried" from "explicitly 0".
            users = referencedUserIds.Where(userById.ContainsKey).Select(id => userById[id]).Select(u => new { id = u.Id, email = u.Email, displayName = u.DisplayName, isActive = u.IsActive, clearanceRank = includePermissions ? u.ClearanceRank : (int?)null }),
            serviceAccounts = referencedServiceAccountIds.Where(serviceAccountById.ContainsKey).Select(id => serviceAccountById[id]).Select(s => new { id = s.Id, name = s.Name, isActive = s.IsActive, clearanceRank = includePermissions ? s.ClearanceRank : (int?)null }),
            // Groups appear only when ACL is included — matched by name on import. Users/serviceAccounts carry
            // natural keys (email/name) so a future import matches by them and mints fresh Guids.
            groups = groupById.Values.Select(g => new { id = g.Id, name = g.Name, clearanceRank = includePermissions ? g.ClearanceRank : (int?)null }),
            // Membership edges among the exported users only (bounded, ADR "Group memberships in export").
            memberships = memberships.Select(m => new { groupId = m.GroupId, userId = m.UserId }),
        }, cancellationToken);

        await WriteJsonLinesAsync(archive, "tree/documents.jsonl", includedDocuments.Select(d => (object)new
        {
            id = d.Id,
            parentId = d.ParentId,
            name = d.Name,
            maskVersionId = d.MaskVersionId,
            sensitivityLabel = LabelName(d.SensitivityLabelId),
            createdByUserId = d.CreatedByUserId,
            createdByServiceAccountId = d.CreatedByServiceAccountId,
            createdAt = d.CreatedAt,
            breaksInheritance = d.BreaksInheritance,
        }), cancellationToken);

        await WriteJsonLinesAsync(archive, "tree/versions.jsonl", exportedVersions.Select(v => (object)new
        {
            id = v.Id,
            documentId = v.DocumentId,
            versionNumber = v.VersionNumber,
            documentDate = v.DocumentDate.ToString("yyyy-MM-dd"),
            filedAt = v.CreatedAt,
            createdByUserId = v.CreatedByUserId,
            createdByServiceAccountId = v.CreatedByServiceAccountId,
            sha256 = v.Sha256Hash,
            fileExtension = Path.GetExtension(v.ObjectKey),
            ocrLanguages = v.OcrLanguages,
            comment = v.Comment,
            blobRef = v.Sha256Hash,
        }), cancellationToken);

        await WriteJsonLinesAsync(archive, "tree/index-data.jsonl", fieldValues.Select(f => (object)new
        {
            documentId = f.DocumentId,
            fieldDefinitionId = f.FieldDefinitionId,
            value = f.Value,
        }), cancellationToken);

        // The chat thread (issue #382) — "tree/comments.jsonl" with a "parentCommentId" field before FormatVersion 2.
        //
        // "mentions" (issue #383) is additive and FormatVersion stays 2 on purpose: an archive written before
        // mentions existed has none to lose, so bumping would only make every archive to date unimportable. The
        // ids are the archive's own user ids, which the import remaps like every other principal reference — the
        // body's "@[id]" tokens are remapped in step.
        await WriteJsonLinesAsync(archive, "tree/chat.jsonl", comments.Select(c => (object)new
        {
            id = c.Id,
            documentId = c.DocumentId,
            parentMessageId = c.ParentMessageId,
            body = c.Body,
            mentions = ChatMentions.Parse(c.Body),
            createdByUserId = c.CreatedByUserId,
            createdByServiceAccountId = c.CreatedByServiceAccountId,
            createdAt = c.CreatedAt,
        }), cancellationToken);

        await WriteJsonLinesAsync(archive, "tree/annotations.jsonl", annotations.Select(a => (object)new
        {
            id = a.Id,
            documentId = a.DocumentId,
            documentVersionId = a.DocumentVersionId,
            pageIndex = a.PageIndex,
            kind = (int)a.Kind,
            positionX = a.PositionX,
            positionY = a.PositionY,
            width = a.Width,
            height = a.Height,
            points = a.Points,
            text = a.Text,
            color = a.Color,
            createdByUserId = a.CreatedByUserId,
            createdByServiceAccountId = a.CreatedByServiceAccountId,
            createdAt = a.CreatedAt,
        }), cancellationToken);

        await WriteJsonLinesAsync(archive, "tree/references.jsonl", references.Select(r => (object)new
        {
            id = r.Id,
            parentFolderId = r.ParentFolderId,
            targetDocumentId = r.TargetDocumentId,
            createdByUserId = r.CreatedByUserId,
            createdByServiceAccountId = r.CreatedByServiceAccountId,
            createdAt = r.CreatedAt,
        }), cancellationToken);

        // ACL grants (opt-in). BreaksInheritance already rides on tree/documents.jsonl, so a document's inherit-
        // vs-own-grants semantics reconstruct exactly on import.
        await WriteJsonLinesAsync(archive, "acl/acl.jsonl", aclEntries.Select(a => (object)new
        {
            documentId = a.DocumentId,
            userId = a.UserId,
            groupId = a.GroupId,
            serviceAccountId = a.ServiceAccountId,
            canSee = a.CanSee,
            canReadContent = a.CanReadContent,
            canEditContent = a.CanEditContent,
            canEditIndexData = a.CanEditIndexData,
            canDelete = a.CanDelete,
            canCreateSubItems = a.CanCreateSubItems,
            canMove = a.CanMove,
            canManagePermissions = a.CanManagePermissions,
            canAnnotate = a.CanAnnotate,
        }), cancellationToken);

        // 10. Blobs — one entry per distinct content hash, streamed from object storage.
        var writtenBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in exportedVersions)
        {
            if (version.Sha256Hash is not { } hash || !writtenBlobs.Add(hash))
            {
                continue;
            }

            var entry = archive.CreateEntry($"blobs/{hash}", CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await using var blob = await _objectStorage.GetObjectAsync(version.ObjectKey, cancellationToken);
            await blob.CopyToAsync(entryStream, cancellationToken);
        }

        await WriteJsonAsync(archive, "manifest.json", new
        {
            formatVersion = FormatVersion,
            exportedAt = DateTimeOffset.UtcNow,
            includesPermissions = includePermissions,
            source = new { tenantId = tenant.Id, tenantName = tenant.Name },
            root = new { documentId = root.Id, name = root.Name },
            filters = new
            {
                documentDateFrom = filters.DocumentDateFrom?.ToString("yyyy-MM-dd"),
                documentDateTo = filters.DocumentDateTo?.ToString("yyyy-MM-dd"),
                filedFrom = filters.FiledFrom,
                filedTo = filters.FiledTo,
                versions = filters.Versions == ExportVersionSelection.ActiveOnly ? "active" : "all",
                createdBy = filters.CreatedBy,
            },
            counts = new
            {
                documents = includedDocuments.Count,
                versions = exportedVersions.Count,
                blobs = writtenBlobs.Count,
                indexValues = fieldValues.Count,
                comments = comments.Count,
                references = references.Count,
                maskVersions = maskVersions.Count,
                principals = referencedUserIds.Count + referencedServiceAccountIds.Count + groupById.Count,
                aclGrants = aclEntries.Count,
                labels = labelsById.Count,
            },
        }, cancellationToken);
    }

    private async Task<List<Document>> CollectSubtreeAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var root = await _dbContext.Documents.SingleAsync(d => d.Id == rootId, cancellationToken);
        var all = new List<Document> { root };
        var level = new List<Guid> { rootId };
        while (level.Count > 0)
        {
            var children = await _dbContext.Documents
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

    private static bool MatchesFilters(
        DocumentVersion version,
        RepositoryExportFilters filters,
        IReadOnlyDictionary<Guid, UserLite> userById,
        IReadOnlyDictionary<Guid, ServiceAccountLite> serviceAccountById)
    {
        if (filters.DocumentDateFrom is { } df && version.DocumentDate < df) return false;
        if (filters.DocumentDateTo is { } dt && version.DocumentDate > dt) return false;
        if (filters.FiledFrom is { } ff && version.CreatedAt < ff) return false;
        if (filters.FiledTo is { } ft && version.CreatedAt > ft) return false;

        if (!string.IsNullOrWhiteSpace(filters.CreatedBy))
        {
            var needle = filters.CreatedBy.Trim();
            var names = new List<string>();
            if (version.CreatedByUserId is { } u && userById.TryGetValue(u, out var user))
            {
                names.Add(user.DisplayName);
                names.Add(user.Email);
            }

            if (version.CreatedByServiceAccountId is { } s && serviceAccountById.TryGetValue(s, out var svc))
            {
                names.Add(svc.Name);
            }

            if (!names.Any(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record UserLite(Guid Id, string Email, string DisplayName, bool IsActive, int ClearanceRank);

    private sealed record ServiceAccountLite(Guid Id, string Name, bool IsActive, int ClearanceRank);

    private sealed record GroupLite(Guid Id, string Name, int ClearanceRank);

    // Asks the single source of truth rather than restating it. The previous hand-written list named three of
    // the eleven well-known masks and had not been touched since the other eight arrived — so a Note, Contact
    // or Appointment exported as NOT well-known, and the importer creates a fresh mask for anything not well
    // known. The imported documents then wore a duplicate mask with a different id, invisible to every
    // WellKnownMaskIds check: containment, the IMAP projection, the clients' type column.
    private static bool IsWellKnown(Guid maskId) => WellKnownMaskIds.All.Contains(maskId);

    private static async Task WriteJsonAsync(ZipArchive archive, string name, object payload, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, payload, Json, cancellationToken);
    }

    private static async Task WriteJsonLinesAsync(ZipArchive archive, string name, IEnumerable<object> rows, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json));
        }
    }
}
