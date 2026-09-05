using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The host's side of <see cref="IModuleArchiveFacade"/> (ADR 0741): the five enumerated operations a
/// module may perform against the archive, running under the calling context's identity and tenant — the
/// facade never switches either. Every write goes through the DbContext's <c>SaveChanges</c>, so the
/// core's invariants (sibling names, containment, required fields, tenant isolation) apply to a module's
/// writes exactly as they do to anyone else's.
/// </summary>
public sealed class ModuleArchiveFacade : IModuleArchiveFacade
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccount;
    private readonly ModuleIdentityAccessor? _identity;
    private readonly IEffectiveRightsCalculator? _rights;
    private readonly IObjectStorageClient? _objectStorage;
    private Guid? _principalId;
    private bool _principalResolved;

    public ModuleArchiveFacade(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUser,
        ICurrentServiceAccountAccessor currentServiceAccount,
        ModuleIdentityAccessor? identity = null,
        IEffectiveRightsCalculator? rights = null,
        IObjectStorageClient? objectStorage = null)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _currentServiceAccount = currentServiceAccount;
        _identity = identity;
        _rights = rights;
        _objectStorage = objectStorage;
    }

    /// <summary>
    /// The reads' gate (ADR 0736): when MODULE code is running (the identity accessor names it — set at
    /// the controller gate, the engine, the rebuild endpoint), a document is visible exactly when the
    /// module's own principal holds CanSee on it. An ungranted module honestly reads NOTHING — the
    /// licensing act is a consent act, and the grants are the consent. A null module id is core-internal
    /// use (license stamping), which stays ungated: the core is not a tenant of its own consent machinery.
    /// Writes deliberately keep the CALLER's attribution — a filed return is the pilot's record; the
    /// module is only the how (owner-decided 2026-09-04).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, EffectiveRights>?> ModuleVisibilityAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (_identity?.ModuleId is not { } moduleId || _rights is null)
        {
            return null; // core-internal: ungated
        }

        if (!_principalResolved)
        {
            _principalId = (await ModulePrincipal.FindAsync(_dbContext, moduleId, cancellationToken))?.Id;
            _principalResolved = true;
        }

        if (_principalId is not { } principalId)
        {
            // No principal means never activated here — nothing was consented, nothing is visible.
            return new Dictionary<Guid, EffectiveRights>();
        }

        return await _rights.GetEffectiveRightsForManyForServiceAccountAsync(principalId, documentIds, cancellationToken);
    }

    private async Task<bool> ModuleMaySeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var visibility = await ModuleVisibilityAsync([documentId], cancellationToken);
        return visibility is null || (visibility.TryGetValue(documentId, out var r) && r.CanSee);
    }

    public async Task<ModuleDocument?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.ParentId, d.Name, d.MaskVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null || !await ModuleMaySeeAsync(documentId, cancellationToken))
        {
            // Ungranted reads exactly like nonexistent (ADR 0543's absence semantics, for module eyes).
            return null;
        }

        var fields = await FieldsOfAsync(document.Id, cancellationToken);
        return new ModuleDocument(
            document.Id,
            document.ParentId,
            document.Name,
            await MaskIdOfAsync(document.MaskVersionId, cancellationToken),
            fields.Joined)
        {
            FieldLists = fields.Lists,
        };
    }

    public async Task<byte[]?> GetDocumentContentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // Same consent gate as the field reads (ADR 0736): a module reads content only of a document its
        // principal may see; ungranted reads as nonexistent.
        if (!await ModuleMaySeeAsync(documentId, cancellationToken))
        {
            return null;
        }

        var pointer = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.CurrentVersionId)
            .SingleOrDefaultAsync(cancellationToken);
        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, pointer, cancellationToken);
        if (version is null)
        {
            return null; // no confirmed version — nothing to parse
        }

        if (_objectStorage is null)
        {
            throw new InvalidOperationException(
                "Content reads need an object-storage client; the host wires one — a test facade that reads content must supply it.");
        }

        await using var content = await _objectStorage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public async Task<IReadOnlyList<ModuleDocument>> GetChildrenAsync(Guid parentDocumentId, Guid maskId, CancellationToken cancellationToken = default)
    {
        // The mask filter walks version → identity, because documents wear a VERSION while a module owns
        // an identity — every version of the module's mask counts (the module may have healed fields in).
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset (provider parity — the model runs on
        // both), and a dossier's children are a bounded set.
        var rows = (await _dbContext.Documents
            .Where(d => d.ParentId == parentDocumentId && d.MaskVersionId != null)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { d, v.MaskId })
            .Where(x => x.MaskId == maskId)
            .Select(x => new { x.d.Id, x.d.ParentId, x.d.Name, x.d.CreatedAt })
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToList();

        var visibility = await ModuleVisibilityAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var children = new List<ModuleDocument>(rows.Count);
        foreach (var row in rows)
        {
            if (visibility is not null && !(visibility.TryGetValue(row.Id, out var r) && r.CanSee))
            {
                continue;
            }

            var childFields = await FieldsOfAsync(row.Id, cancellationToken);
            children.Add(new ModuleDocument(row.Id, row.ParentId, row.Name, maskId, childFields.Joined) { FieldLists = childFields.Lists });
        }

        return children;
    }

    public async Task<IReadOnlyList<ModuleDocument>> GetByMaskAsync(Guid maskId, CancellationToken cancellationToken = default)
    {
        // The rebuild's subject enumeration (ADR 0738) — same version → identity walk and same in-memory
        // ordering as the children read, for the same provider-parity reason.
        var rows = (await _dbContext.Documents
            .Where(d => d.MaskVersionId != null)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { d, v.MaskId })
            .Where(x => x.MaskId == maskId)
            .Select(x => new { x.d.Id, x.d.ParentId, x.d.Name, x.d.CreatedAt })
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToList();

        var visibility = await ModuleVisibilityAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var documents = new List<ModuleDocument>(rows.Count);
        foreach (var row in rows)
        {
            if (visibility is not null && !(visibility.TryGetValue(row.Id, out var r) && r.CanSee))
            {
                continue;
            }

            var documentFields = await FieldsOfAsync(row.Id, cancellationToken);
            documents.Add(new ModuleDocument(row.Id, row.ParentId, row.Name, maskId, documentFields.Joined) { FieldLists = documentFields.Lists });
        }

        return documents;
    }

    public async Task<Guid> CreateDocumentAsync(Guid parentDocumentId, Guid maskId, string name, IReadOnlyDictionary<string, string>? fields = null, CancellationToken cancellationToken = default)
    {
        var parent = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == parentDocumentId, cancellationToken)
            ?? throw new ArgumentException($"Parent document {parentDocumentId} does not exist.", nameof(parentDocumentId));

        var maskVersion = await CurrentMaskVersionAsync(maskId, cancellationToken);
        var (userId, serviceAccountId) = CallerIdentity();

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            ParentId = parent.Id,
            Name = name,
            MaskVersionId = maskVersion.Id,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.Documents.Add(document);
        AddFieldValues(document, maskVersion, fields);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return document.Id;
    }

    public async Task SetFieldsAsync(Guid documentId, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            ?? throw new ArgumentException($"Document {documentId} does not exist.", nameof(documentId));
        if (document.MaskVersionId is not { } maskVersionId)
        {
            throw new InvalidOperationException($"Document {documentId} wears no mask; a module can only set fields its mask defines.");
        }

        foreach (var (name, value) in fields)
        {
            // By NAME within the document's own mask version — never by DocumentId alone, which returns an
            // arbitrary field (the vCard-UID-became-a-phone-number lesson).
            var definitionId = await _dbContext.FieldDefinitions
                .Where(f => f.MaskVersionId == maskVersionId && f.Name == name)
                .Select(f => (Guid?)f.Id)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException($"The document's mask defines no field named '{name}'.", nameof(fields));

            var existing = await _dbContext.FieldValues
                .SingleOrDefaultAsync(v => v.DocumentId == documentId && v.FieldDefinitionId == definitionId, cancellationToken);
            if (existing is null)
            {
                _dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = documentId,
                    FieldDefinitionId = definitionId,
                    Value = value,
                });
            }
            else
            {
                existing.Value = value;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetFieldListAsync(Guid documentId, string fieldName, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            ?? throw new ArgumentException($"Document {documentId} does not exist.", nameof(documentId));
        if (document.MaskVersionId is not { } maskVersionId)
        {
            throw new InvalidOperationException($"Document {documentId} wears no mask; a module can only set fields its mask defines.");
        }

        // Same by-name-within-the-mask-version resolution as the single-value write (the vCard-UID
        // lesson); a replace-write like the core's own metadata PUT — the rows afterwards ARE the list.
        var definitionId = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.Name == fieldName)
            .Select(f => (Guid?)f.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException($"The document's mask defines no field named '{fieldName}'.", nameof(fieldName));

        var existing = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId && v.FieldDefinitionId == definitionId)
            .ToListAsync(cancellationToken);
        _dbContext.FieldValues.RemoveRange(existing);
        for (var ordinal = 0; ordinal < values.Count; ordinal++)
        {
            _dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = documentId,
                FieldDefinitionId = definitionId,
                Value = values[ordinal],
                Ordinal = ordinal,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RenameDocumentAsync(Guid documentId, string name, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            ?? throw new ArgumentException($"Document {documentId} does not exist.", nameof(documentId));

        // The sibling-name invariant fires in SaveChanges like anyone else's rename (ABI 0.2, #1014).
        document.Name = name;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateReferenceAsync(Guid targetDocumentId, Guid intoFolderId, CancellationToken cancellationToken = default)
    {
        var target = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == targetDocumentId, cancellationToken)
            ?? throw new ArgumentException($"Target document {targetDocumentId} does not exist.", nameof(targetDocumentId));
        var (userId, serviceAccountId) = CallerIdentity();

        _dbContext.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = target.TenantId,
            ParentFolderId = intoFolderId,
            TargetDocumentId = targetDocumentId,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private (Guid? UserId, Guid? ServiceAccountId) CallerIdentity()
    {
        if (_currentUser.UserId is { } userId)
        {
            return (userId, null);
        }

        if (_currentServiceAccount.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        throw new InvalidOperationException("The archive facade requires a calling identity — it never invents one.");
    }

    private async Task<MaskVersion> CurrentMaskVersionAsync(Guid maskId, CancellationToken cancellationToken) =>
        await _dbContext.MaskVersions
            .Where(v => v.MaskId == maskId && v.IsCurrent)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new ArgumentException($"Mask {maskId} has no current version in this tenant — was the module activated here?", nameof(maskId));

    private void AddFieldValues(Document document, MaskVersion maskVersion, IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null)
        {
            return;
        }

        foreach (var (name, value) in fields)
        {
            var definition = _dbContext.FieldDefinitions.Local
                    .SingleOrDefault(f => f.MaskVersionId == maskVersion.Id && f.Name == name)
                ?? _dbContext.FieldDefinitions
                    .Where(f => f.MaskVersionId == maskVersion.Id && f.Name == name)
                    .SingleOrDefault()
                ?? throw new ArgumentException($"Mask '{maskVersion.Name}' defines no field named '{name}'.", nameof(fields));

            _dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = definition.Id,
                Value = value,
            });
        }
    }

    private async Task<Guid?> MaskIdOfAsync(Guid? maskVersionId, CancellationToken cancellationToken) =>
        maskVersionId is null
            ? null
            : await _dbContext.MaskVersions.Where(v => v.Id == maskVersionId).Select(v => (Guid?)v.MaskId).SingleOrDefaultAsync(cancellationToken);

    private async Task<(IReadOnlyDictionary<string, string> Joined, IReadOnlyDictionary<string, IReadOnlyList<string>> Lists)> FieldsOfAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value, v.Ordinal })
            .OrderBy(x => x.Name).ThenBy(x => x.Ordinal)
            .ToListAsync(cancellationToken);

        var groups = rows.GroupBy(r => r.Name, StringComparer.Ordinal).ToList();

        // Both wire shapes from one query (ABI 0.2, #1014): FieldLists is the faithful one (ordinal
        // order); the "+"-joined Fields stays for 0.1 readers.
        return (
            groups.ToDictionary(g => g.Key, g => string.Join("+", g.Select(r => r.Value)), StringComparer.Ordinal),
            groups.ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList(), StringComparer.Ordinal));
    }
}
