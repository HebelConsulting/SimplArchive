using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Get-or-create the logged-in user's personal repository (ADR "Per-user personal repository") — a root
/// <see cref="Document"/> flagged with <c>PersonalOfUserId</c>, named after its owner (ADR 0671), Folder-masked, with a
/// full-rights ACL grant to the user. Extracted from <c>PersonalRepositoryController</c> so the WebDAV gateway
/// (which nests the Intray / Check-out folders under Personal) can ensure it exists too. Idempotent.
/// </summary>
public sealed class PersonalRepositoryProvisioner
{
    /// <summary>
    /// What a personal space was called before it was named after its owner (ADR 0671) — kept because spaces
    /// provisioned earlier still carry it: the rename is not backfilled, so "Personal" and a display name are
    /// both live names for the same kind of node. Nothing NEW is named this.
    /// </summary>
    public const string LegacyPersonalRepositoryName = "Personal";

    /// <summary>The default subfolder every personal repository is seeded with (ADR "My Documents in the personal space").</summary>
    public const string MyDocumentsFolderName = PersonalFolders.MyDocuments;

    // The typed notebook (#562 slice 5, renamed with its mask by #564). The IMAP layer keeps projecting it as
    // the root mailbox literally named "Notes" — that name is Apple's convention for where notes live, not
    // ours, and an account that already works discovers the mailbox by it. So the rename stops at the wire:
    // the folder, the tree, WebDAV and both clients say Notebook; IMAP says Notes.
    public const string NotebookFolderName = "Notebook";

    // What the folder was called before that rename, so an already-provisioned personal space is healed rather
    // than given a second one beside the first (the same trap #574 hit with maskless Notes).
    public const string LegacyNotesFolderName = "Notes";

    // The typed calendar/contact folders (#564) — every user gets one of each, and CalDAV/CardDAV list them
    // first in the home set. Unlike these defaults, further typed folders may be created anywhere in the tree.
    public const string MyCalendarFolderName = PersonalFolders.MyCalendar;

    /// <summary>
    /// The mailbox node LMTP creates on first delivery (#617). Named here with the other personal folders
    /// rather than as a literal at its one call site, so the set of names a personal space can hold is
    /// readable in one place.
    /// </summary>
    public const string MyMailboxFolderName = PersonalFolders.MyMailbox;

    /// <summary>What the mailbox was called before 2026-08-19.</summary>
    public const string LegacyMyEmailsFolderName = "My eMails";

    public const string MyAddressbookFolderName = PersonalFolders.MyAddressbook;

    /// <summary>
    /// What the addressbook folder was called before 2026-08-19. Matched so an already-provisioned space is
    /// RENAMED rather than given a second, empty folder beside the one holding the user's contacts (#574).
    /// </summary>
    public const string LegacyMyContactsFolderName = "My Contacts";

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
    public async Task<Document> EnsureAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken,
        Func<string, Guid>? idFor = null, DateTimeOffset? createdAt = null)
    {
        // createdAt rides along with idFor (#832): a reseeded demo space must not wear the boot minute as its
        // folders' Created stamps, or every manual capture renders different dates. Real users keep real time.
        //
        // Each folder gets its own millisecond off the base instant, because listings order by
        // (CreatedAt, Id) and a real user's ids are random: with all three folders on ONE instant, the tie
        // fell to the ids and the tree showed My Addressbook before My Calendar on some machines and not
        // others — the very coin flip this parameter exists to remove, reintroduced one level down
        // (CI caught it as PersonalRepositoryTests expecting the creation order).
        var at = createdAt ?? DateTimeOffset.UtcNow;
        // idFor (#781): the demo seeder derives the space's folder ids from stable slugs so the kiosk's nightly
        // reseed keeps every client-visible identity. Null for real users — their recreated space must READ as
        // recreated (a fresh id is what moves UIDVALIDITY and the DAV collection identity).
        var root = await EnsureRootAsync(userId, tenantId, cancellationToken, idFor?.Invoke("root"), at);
        // My Documents goes through the same helper as the other two now: it used to have a near-copy of its
        // own, differing only in that it stamped the plain Folder mask. It wears its OWN mask since #634, and
        // restampFromMaskId is what moves an already-provisioned one off Folder — a space created before that
        // mask existed has a correctly-typed folder by the old rule and a wrongly-typed one by the new.
        await EnsureTypedFolderAsync(root, tenantId, userId, MyDocumentsFolderName, WellKnownMaskIds.MyDocuments, cancellationToken, at.AddMilliseconds(1), restampFromMaskId: WellKnownMaskIds.Folder, id: idFor?.Invoke("my-documents"));
        await EnsureTypedFolderAsync(root, tenantId, userId, MyCalendarFolderName, WellKnownMaskIds.Calendar, cancellationToken, at.AddMilliseconds(2), id: idFor?.Invoke("my-calendar"));
        await EnsureTypedFolderAsync(root, tenantId, userId, MyAddressbookFolderName, WellKnownMaskIds.Addressbook, cancellationToken, at.AddMilliseconds(3), LegacyMyContactsFolderName, id: idFor?.Invoke("my-addressbook"));
        return root;
    }

    private async Task<Document> EnsureRootAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken, Guid? id, DateTimeOffset at)
    {
        var existing = await _dbContext.Documents.SingleOrDefaultAsync(d => d.PersonalOfUserId == userId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // Named after its owner (ADR 0671). Read here rather than passed in, because every caller has the id and
        // none of them has the person — and a space provisioned with the wrong name is not something a later
        // rename fixes for anyone who has already mounted it.
        var owner = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.Email })
            .SingleAsync(cancellationToken);

        var document = new Document
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = null,
            Name = PersonalSpaceName.For(owner.DisplayName, owner.Email),
            PersonalOfUserId = userId,
            // Tenant-EXPLICIT (ADR 0590): this method already has the tenant, and resolving the mask through the
            // ambient one instead made a personal repository come out with no mask whenever the caller had no
            // current tenant set — the same defect ADR 0582 fixed for a tenant's first repository. Taking it from
            // the parameter removes the dependency on caller context altogether.
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, WellKnownMaskIds.UserFolder, cancellationToken)
                ?? await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, cancellationToken),
            CreatedByUserId = userId,
            CreatedAt = at,
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
            CreatedAt = at,
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
    /// Idempotently ensures the personal repository has a TYPED child folder wearing <paramref name="folderMaskId"/>
    /// — "Notes" for the IMAP notes mailbox (#562 slice 5), "My Calendar"/"My Addressbook" for CalDAV/CardDAV
    /// (#564). One implementation for all three: they differ only in name and mask. Same concurrency posture
    /// as "My Documents".
    /// </summary>
    private async Task EnsureTypedFolderAsync(
        Document root, Guid tenantId, Guid userId, string name, Guid folderMaskId, CancellationToken cancellationToken,
        DateTimeOffset at, string? legacyName = null, Guid? restampFromMaskId = null, Guid? id = null)
    {
        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, tenantId, folderMaskId, cancellationToken);

        var existing = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.ParentId == root.Id && d.Name == name, cancellationToken);

        // Nothing under the new name — but an already-provisioned space has it under the OLD one, and looking
        // only for the new name would give that user a second, empty folder beside the one holding their notes
        // (#574's trap: a grow-later seed that only ever exercises the fresh-volume path). Rename in place.
        if (existing is null && legacyName is not null)
        {
            existing = await _dbContext.Documents
                .FirstOrDefaultAsync(d => d.ParentId == root.Id && d.Name == legacyName, cancellationToken);
            if (existing is not null)
            {
                existing.Name = name;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        if (existing is not null)
        {
            // Heal a maskless typed folder: a provisioning run whose tenant predated this mask (an upgraded
            // deployment) created the folder with no mask at all — in which state it neither projects onto its
            // protocol surface nor enforces its typed containment.
            if (existing.MaskVersionId is null && maskVersionId is not null)
            {
                existing.MaskVersionId = maskVersionId;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (restampFromMaskId is { } fromMaskId && maskVersionId is not null && existing.MaskVersionId is { } currentVersionId)
            {
                // A folder wearing the mask this one USED to wear. Distinct from the maskless heal above: the
                // folder is correctly typed by the old rule and wrongly typed by the new one, which no
                // "is it null?" check can see. Restamped only from the ONE mask named — a blanket restamp would
                // claim any folder that happened to be sitting here.
                var wearsOldMask = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
                    .AnyAsync(v => v.Id == currentVersionId && v.MaskId == fromMaskId, cancellationToken);
                if (wearsOldMask)
                {
                    existing.MaskVersionId = maskVersionId;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            return;
        }

        _dbContext.Documents.Add(new Document
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = root.Id,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = at,
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
