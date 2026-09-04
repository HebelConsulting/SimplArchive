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

    public ModuleArchiveFacade(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUser,
        ICurrentServiceAccountAccessor currentServiceAccount)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _currentServiceAccount = currentServiceAccount;
    }

    public async Task<ModuleDocument?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.ParentId, d.Name, d.MaskVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }

        return new ModuleDocument(
            document.Id,
            document.ParentId,
            document.Name,
            await MaskIdOfAsync(document.MaskVersionId, cancellationToken),
            await FieldsOfAsync(document.Id, cancellationToken));
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

        var children = new List<ModuleDocument>(rows.Count);
        foreach (var row in rows)
        {
            children.Add(new ModuleDocument(row.Id, row.ParentId, row.Name, maskId, await FieldsOfAsync(row.Id, cancellationToken)));
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

        var documents = new List<ModuleDocument>(rows.Count);
        foreach (var row in rows)
        {
            documents.Add(new ModuleDocument(row.Id, row.ParentId, row.Name, maskId, await FieldsOfAsync(row.Id, cancellationToken)));
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

    private async Task<IReadOnlyDictionary<string, string>> FieldsOfAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value, v.Ordinal })
            .OrderBy(x => x.Name).ThenBy(x => x.Ordinal)
            .ToListAsync(cancellationToken);

        // List fields collapse to a "+"-joined value in v0.1 — the facade reads facts, not editors.
        return rows
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join("+", g.Select(r => r.Value)), StringComparer.Ordinal);
    }
}
