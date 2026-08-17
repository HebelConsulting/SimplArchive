using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Get-or-create the logged-in user's personal repository (ADR "Per-user personal repository") — a root
/// <see cref="Document"/> flagged with <c>PersonalOfUserId</c>, named "Personal", Folder-masked, with a
/// full-rights ACL grant to the user. Extracted from <c>PersonalRepositoryController</c> so the WebDAV gateway
/// (which nests the Intray / Check-out folders under Personal) can ensure it exists too. Idempotent.
/// </summary>
public sealed class PersonalRepositoryProvisioner
{
    public const string PersonalRepositoryName = "Personal";

    /// <summary>The default subfolder every personal repository is seeded with (ADR "My Documents in the personal space").</summary>
    public const string MyDocumentsFolderName = "My Documents";

    // The typed notes folder (#562 slice 5) — the IMAP layer projects it as the root "Notes" mailbox.
    public const string NotesFolderName = "Notes";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IAuditRecorder _audit;

    public PersonalRepositoryProvisioner(SimplArchiveDbContext dbContext, IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _audit = audit;
    }

    /// <summary>
    /// Returns the user's personal repository, creating it on first call. Also idempotently ensures the default
    /// "My Documents" subfolder exists — so a pre-existing personal repo gains it the next time it's accessed
    /// (the "backfill existing" behaviour), not only freshly-created ones.
    /// </summary>
    public async Task<Document> EnsureAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var root = await EnsureRootAsync(userId, tenantId, cancellationToken);
        await EnsureMyDocumentsAsync(root, tenantId, userId, cancellationToken);
        await EnsureNotesAsync(root, tenantId, userId, cancellationToken);
        return root;
    }

    private async Task<Document> EnsureRootAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Documents.SingleOrDefaultAsync(d => d.PersonalOfUserId == userId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = null,
            Name = PersonalRepositoryName,
            PersonalOfUserId = userId,
            // Tenant-EXPLICIT (ADR 0590): this method already has the tenant, and resolving the mask through the
            // ambient one instead made a personal repository come out with no mask whenever the caller had no
            // current tenant set — the same defect ADR 0582 fixed for a tenant's first repository. Taking it from
            // the parameter removes the dependency on caller context altogether.
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, WellKnownMaskIds.UserFolder, cancellationToken)
                ?? await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, cancellationToken),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.Documents.Add(document);

        _dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            UserId = userId,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanDelete = true,
            CanCreateSubItems = true,
            CanManagePermissions = true,
            CanMove = true,
            CanAnnotate = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent Ensure won the unique (TenantId, PersonalOfUserId) index — return the winner's row.
            _dbContext.ChangeTracker.Clear();
            return await _dbContext.Documents.SingleAsync(d => d.PersonalOfUserId == userId, cancellationToken);
        }

        // Audit only the actual creation (ADR "Audit tenant-settings, inbox filing + personal-repository creation").
        await _audit.RecordAsync(AuditActions.RepositoryCreated, "Document", document.Id, document.Name, "Personal repository created", cancellationToken: cancellationToken);
        return document;
    }

    /// <summary>
    /// Idempotently ensures the personal repository has a "My Documents" child folder. A no-op once it exists;
    /// the folder inherits the root's full-rights ACL (the root doesn't break inheritance). Best-effort on a
    /// concurrent create — a second caller's duplicate is swallowed (the sibling-name guard / a later Ensure
    /// call keeps it single).
    /// </summary>
    private async Task EnsureMyDocumentsAsync(Document root, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Documents
            .AnyAsync(d => d.ParentId == root.Id && d.Name == MyDocumentsFolderName, cancellationToken);
        if (exists)
        {
            return;
        }

        _dbContext.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = root.Id,
            Name = MyDocumentsFolderName,
            // Tenant-EXPLICIT for the same reason as the root above (ADR 0590) — the ambient-tenant overload
            // yields a maskless folder whenever the caller has no current tenant set.
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, cancellationToken),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // A concurrent Ensure already created "My Documents" (the SaveChanges sibling-name guard, ADR 0177,
            // throws InvalidOperationException; a future unique-index would throw DbUpdateException). Either way
            // the folder now exists — drop this call's pending insert and move on.
            _dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Idempotently ensures the personal repository has a "Notes" child wearing the NoteFolder mask (#562
    /// slice 5) — the TYPED folder the notes clients sync into, projected as the root "Notes" mailbox over
    /// IMAP. Same concurrency posture as "My Documents".
    /// </summary>
    private async Task EnsureNotesAsync(Document root, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var noteFolderMaskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, WellKnownMaskIds.NoteFolder, cancellationToken);

        var existing = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.ParentId == root.Id && d.Name == NotesFolderName, cancellationToken);
        if (existing is not null)
        {
            // Heal a maskless Notes folder: a provisioning run whose tenant predated the NoteFolder mask
            // (an upgraded deployment) created the folder with no mask at all — in which state it neither
            // projects as the root "Notes" IMAP mailbox nor enforces its typed containment.
            if (existing.MaskVersionId is null && noteFolderMaskVersionId is not null)
            {
                existing.MaskVersionId = noteFolderMaskVersionId;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        _dbContext.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = root.Id,
            Name = NotesFolderName,
            MaskVersionId = noteFolderMaskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _dbContext.ChangeTracker.Clear();
        }
    }
}
