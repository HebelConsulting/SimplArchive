using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Files a typed item's bytes — a new entry in a collection, or a new version of one that exists.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because a THIRD caller needed it (#1133): editing one occurrence of a series both amends the
/// series and creates a new entry beside it, in one request, so the two halves cannot be two controllers'
/// private methods. The sequence — object first, then a Pending version, then the shared finalizer — is the
/// part a reader never re-reads and the part that goes subtly wrong when copied.
/// </para>
/// <para>
/// <b>Pending + the finalizer, never a hand-written Confirmed version.</b> The status is guarded by a CHECK
/// constraint, and the finalizer is what classifies the item and extracts its index fields — the UID among
/// them, which is what a later DAV sync matches on.
/// </para>
/// </remarks>
public sealed class TypedItemWriter(
    SimplArchiveDbContext dbContext,
    IObjectStorageClient storage,
    DocumentFinalizer finalizer)
{
    /// <summary>A new document filed into <paramref name="folder"/>, with its first version.</summary>
    /// <remarks>
    /// The document is created MASKLESS on purpose: the finalizer classifies a .vcf as a Contact and an .ics
    /// as an Appointment once the bytes are there, and a typed folder admits only those. Stamping a mask here
    /// would be guessing at what classification is about to decide.
    /// </remarks>
    public async Task<Document> CreateAsync(
        Document folder,
        string name,
        string blob,
        string extension,
        string contentType,
        Guid? createdByUserId,
        Guid? createdByServiceAccountId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var storageFolderId = Guid.NewGuid();

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = folder.TenantId,
            ParentId = folder.Id,
            Name = await UniqueNameAsync(folder.Id, name, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteVersionAsync(document, blob, extension, contentType, createdByUserId, cancellationToken);
        return document;
    }

    /// <summary>A new version of an existing item, carrying <paramref name="blob"/>.</summary>
    public async Task WriteVersionAsync(
        Document document,
        string blob,
        string extension,
        string contentType,
        Guid? createdByUserId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(
            document.TenantId, now, document.StorageFolderId, versionId, extension);

        await storage.PutObjectAsync(
            objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blob)), contentType, cancellationToken);

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = document.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        dbContext.DocumentVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);
        await finalizer.FinalizeAsync(version, cancellationToken);
    }

    /// <summary>A name no sibling already holds — the DbContext refuses a clash, and a create should not.</summary>
    private async Task<string> UniqueNameAsync(Guid folderId, string name, CancellationToken cancellationToken)
    {
        var taken = await dbContext.Documents
            .Where(d => d.ParentId == folderId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
