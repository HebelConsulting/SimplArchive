using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Masks;

// See ADR "Mask creation endpoint". VersionNumber/IsCurrent on the created MaskVersion are left unset —
// SimplArchiveDbContext.SaveChanges assigns them automatically (ADR "Mask name uniqueness across
// versions"), same precedent as every other MaskVersion creation path.
public class WellKnownMaskSeeder : IWellKnownMaskSeeder
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ILogger<WellKnownMaskSeeder> _logger;

    public WellKnownMaskSeeder(SimplArchiveDbContext dbContext, ILogger<WellKnownMaskSeeder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    private record FieldSpec(string Name, FieldDataType DataType, bool IsRequired);

    private static readonly FieldSpec ColourField = new("Colour", FieldDataType.Text, IsRequired: false);

    public async Task EnsureWellKnownMasksAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Folder, "Folder", [], cancellationToken);

        // A repository is a root document (ADR 0200). The mask carries no fields — like Folder, which it is a
        // copy of in everything but identity — and exists so a repository can SAY it is one (ADR 0627).
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Repository, "Repository", [], cancellationToken);

        // "Short Description" and "Doc Date" were removed — the former duplicates Document.Name (a document
        // is named after its file, ADR "Drag-and-drop document upload"), the latter duplicates the real
        // DocumentVersion.DocumentDate issuing date (ADR "System-field search"). See ADR "Drop redundant
        // Short Description / Doc Date mask fields".
        // The personal-space mask (ADR 0590) — optional fields, because a personal space must exist before
        // anybody has filled anything in.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.UserFolder, "User Folder",
        [
            new FieldSpec("Full name", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Title", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Degree", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Position", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Department", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Company", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Office", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Location", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Abbreviation", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Telephone", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Mobile", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Fax", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Email", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.BasicEntry, "Basic Entry",
        [
            new FieldSpec("Keywords", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        // Filled automatically on upload of an .eml/.msg (ADR "Email auto-classification"). "Entry ID" is
        // the RFC 5322 Message-ID; Cc/Date/Entry ID are optional (not every message has them).
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.EMail, "eMail",
        [
            new FieldSpec("From", FieldDataType.Text, IsRequired: true),
            new FieldSpec("To", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Cc", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Subject", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Date", FieldDataType.Date, IsRequired: false),
            new FieldSpec("Entry ID", FieldDataType.Text, IsRequired: false),
            // Threading + provenance (ADR 0587). "Conversation ID" is RFC 5322 threading (References/In-Reply-To)
            // and is meaningful for any mail client; "Mailbox path" and "Reference" are the folder a message was
            // filed from and the filing reference — an import fills them, a manual filing leaves them empty.
            // Optional, all three: a mail without them must still classify.
            new FieldSpec("Conversation ID", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Mailbox path", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Reference", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        // The Notebook family (#562 slice 5; sections added by #564). Notebook and Section are both fieldless —
        // they type the folder; the fields live on the notes. "Note UUID" is the
        // X-Universally-Unique-Identifier correlation key (an edit from a notes client re-appends under the
        // same UUID and becomes a new VERSION); "Modified" is the newest version's client-stamped time. Field
        // set decided as a guess-to-validate in the epic's interview.
        //
        // "Note Folder" → "Notebook" is a RENAME, not a new mask: the id is unchanged, so RenameIfNeededAsync
        // heals every already-seeded tenant in place and no document moves.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Notebook, "Notebook", [], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.NotebookSection, "Section", [], cancellationToken);

        // Fieldless by consequence, not by omission: ADR 0627 gave this mask host/username/password fields
        // because we were an IMAP client then, and ADR 0628 made us the destination, so there is no account to
        // log into and the address is derived rather than stored. A personal space admits at most one
        // (WellKnownMaskIds.ChildCardinalityRules), which the DbContext enforces.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Mailbox, "Mailbox", [], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Note, "Note",
        [
            new FieldSpec("Note UUID", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Modified", FieldDataType.Date, IsRequired: false),
        ], cancellationToken);

        // The collection's DEFAULT colour is an ordinary optional field on the FOLDER mask (#564 slice 2,
        // ADR 0620), so every collection carries its own and a per-user override (DavCollectionColors) sits
        // on top. Optional, so #579's field-healing can add it to tenants seeded before this slice.
        // The CalDAV/CardDAV pairs (#564, ADR 0619) — same shape as the Notes pair: the folder masks are
        // fieldless (they type the folder, and unlike Notes they may sit anywhere in the tree), the item masks
        // carry the fields extracted from the stored .vcf/.ics. The UID fields are the correlation keys a DAV
        // PUT matches on to make a new version rather than a second document. Recurrence stays opaque in the
        // .ics — "Start"/"End" are the first occurrence's, for search and listing only.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Addressbook, "Addressbook", [ColourField], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Contact, "Contact",
        [
            new FieldSpec("Contact UID", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Full name", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Email", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Phone", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Organization", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Appointment, "Appointment",
        [
            new FieldSpec("Event UID", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Start", FieldDataType.Date, IsRequired: false),
            new FieldSpec("End", FieldDataType.Date, IsRequired: false),
            new FieldSpec("Location", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        // AFTER the item, deliberately: MaskVersions has a unique (TenantId, Name) index, and this folder mask
        // is taking the name "Calendar" that the ITEM mask held before the rename to Appointment. Renaming the
        // item out of the way first is what stops the heal colliding with itself on every existing tenant.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Calendar, "Calendar", [ColourField], cancellationToken);

        // Last, because it needs every mask above to exist: move repositories that predate the Repository mask
        // off Folder and onto it. Idempotent, and it is what makes the lockstep invariant true of the data that
        // is already there rather than only of what is created from now on.
        await BackfillRepositoryMaskAsync(tenantId, cancellationToken);
    }

    private async Task EnsureMaskAsync(Guid tenantId, Guid maskId, string name, IReadOnlyList<FieldSpec> fields, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters(["TenantFilter"]) — this Where clause is already explicitly scoped by the
        // tenantId parameter, so the automatic tenant filter is redundant here and, worse, wrong whenever
        // the caller has no ICurrentTenantAccessor.TenantId set (e.g. a PlatformAdministrator creating a
        // brand-new tenant, ADR "Tenant onboarding and platform-admin mechanism") — that filter's
        // predicate is `TenantId == null`, which never matches any real row, making this check always
        // report "not found" regardless of the real data.
        if (await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"]).AnyAsync(m => m.TenantId == tenantId && m.Id == maskId, cancellationToken))
        {
            await RenameIfNeededAsync(tenantId, maskId, name, cancellationToken);
            await AddMissingFieldsAsync(tenantId, maskId, fields, cancellationToken);
            return;
        }

        _dbContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });

        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = maskId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        _dbContext.MaskVersions.Add(maskVersion);

        foreach (var field in fields)
        {
            _dbContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskVersionId = maskVersion.Id,
                Name = field.Name,
                DataType = field.DataType,
                IsRequired = field.IsRequired,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // A well-known mask that already exists may still be MISSING FIELDS: its field set is fixed at the moment the
    // tenant was seeded, and every field added to a well-known mask afterwards reached only tenants provisioned
    // later. That is how ADR 0587's three e-mail fields (`Conversation ID`, `Mailbox path`, `Reference`) never
    // arrived anywhere they were needed — the mask existed, so the check above returned and the fields were never
    // looked at. The startup loop in Program.cs already visits every tenant for exactly this class of drift; this
    // extends the probe it performs from "does the mask exist" to "does it have all of its fields".
    //
    // The fields are added to the mask's CURRENT version rather than by minting a new one. Well-known masks are
    // app-owned schema, not user-authored masks, and an OPTIONAL field is purely additive: no existing document
    // becomes invalid, no stored value changes meaning, and nothing needs re-pointing. That is why the guard below
    // matters — a REQUIRED field is not additive. It would retroactively invalidate every document already on the
    // mask (the required-field validation, ADR 0176, runs on mask (re)assignment), so this refuses to add one
    // silently. Adding a required field to a well-known mask is a deliberate data migration, not a startup probe.
    private async Task AddMissingFieldsAsync(Guid tenantId, Guid maskId, IReadOnlyList<FieldSpec> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0)
        {
            return;
        }

        var currentVersion = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(mv => mv.TenantId == tenantId && mv.MaskId == maskId && mv.IsCurrent)
            .Select(mv => mv.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentVersion == Guid.Empty)
        {
            return; // a mask with no current version is a broken row this probe must not compound
        }

        var existing = await _dbContext.FieldDefinitions.IgnoreQueryFilters(["TenantFilter"])
            .Where(f => f.TenantId == tenantId && f.MaskVersionId == currentVersion)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);

        var missing = fields.Where(f => !existing.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var required = missing.Where(f => f.IsRequired).Select(f => f.Name).ToList();
        if (required.Count > 0)
        {
            // Loud rather than silent: the alternative is invalidating documents at startup, and the alternative
            // to that is skipping quietly, which would leave the mask permanently wrong with nobody told.
            throw new RequiredFieldAddedToWellKnownMaskException(maskId, tenantId, required);
        }

        foreach (var field in missing)
        {
            _dbContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskVersionId = currentVersion,
                Name = field.Name,
                DataType = field.DataType,
                IsRequired = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        _logger.LogInformation("Added {Count} missing field(s) to well-known mask {MaskId} for tenant {TenantId}: {Fields}",
            missing.Count, maskId, tenantId, string.Join(", ", missing.Select(f => f.Name)));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Heals a well-known mask's NAME on an upgrade. A tenant seeded before a rename keeps the old name
    /// forever otherwise — the field-heal beside this covers what a mask CONTAINS but never what it is
    /// CALLED, and "Contact Folder"/"Calendar Folder" were renamed to "Addressbook"/"Calendar" (with their
    /// items "Contact"/"Appointment") after the first tenants had already been seeded. Renamed in place on
    /// the current version rather than minting a new one, for the same reason the field-heal does: a
    /// well-known mask stays on exactly one version.
    /// </summary>
    private async Task RenameIfNeededAsync(Guid tenantId, Guid maskId, string name, CancellationToken cancellationToken)
    {
        var current = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == maskId && v.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.Name == name)
        {
            return;
        }

        _logger.LogInformation("Renaming well-known mask {MaskId} for tenant {TenantId} from {OldName} to {NewName}",
            maskId, tenantId, current.Name, name);
        current.Name = name;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Moves existing repositories off the Folder mask and onto Repository (ADR 0627, #596).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every repository created before that ADR wears <c>Folder</c>, which the lockstep invariant now forbids
    /// — so this is not an optional tidy-up, it is what makes the invariant true of data that already exists.
    /// A seed that only ever grows would leave every pre-existing tenant behind, and a fresh-volume test
    /// cannot see it because the only tenants it creates are new ones. That is exactly how #574 was missed.
    /// </para>
    /// <para>
    /// Only roots wearing <c>Folder</c> are touched. A personal space wears <c>User Folder</c> (ADR 0590) and
    /// is left alone; a root that is already <c>Repository</c> is a no-op, so this is idempotent and safe to
    /// run on every startup.
    /// </para>
    /// </remarks>
    private async Task BackfillRepositoryMaskAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var folderVersionIds = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Folder)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);
        if (folderVersionIds.Count == 0)
        {
            return;
        }

        var repositoryVersionId = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Repository && v.IsCurrent)
            .Select(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (repositoryVersionId == Guid.Empty)
        {
            return;
        }

        var roots = await _dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .Where(d => d.TenantId == tenantId
                && d.ParentId == null
                && d.MaskVersionId != null
                && folderVersionIds.Contains(d.MaskVersionId.Value))
            .ToListAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return;
        }

        foreach (var root in roots)
        {
            root.MaskVersionId = repositoryVersionId;
        }

        _logger.LogInformation(
            "Moved {Count} existing repositories in tenant {TenantId} from the Folder mask to Repository.",
            roots.Count, tenantId);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
