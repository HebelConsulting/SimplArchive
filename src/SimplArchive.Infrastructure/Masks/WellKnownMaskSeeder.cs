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

    // IsList defaults to false so the 37 existing specs read exactly as before; a list field states it
    // (#703). The heal below carries it, for the same reason it carries DataType — a fact added to a
    // well-known mask reaches only tenants provisioned afterwards unless the heal does.
    private record FieldSpec(string Name, FieldDataType DataType, bool IsRequired, bool IsList = false);

    private static readonly FieldSpec ColourField = new("Colour", FieldDataType.Text, IsRequired: false);

    /// <summary>
    /// The Mailbox mask's address-claims field (#703). A NAME is a well-known field's identity — the heal
    /// matches by name — so the one constant is shared with everything that must find the field again: the
    /// claims enforcement in the metadata controller, and (next slice) LMTP delivery.
    /// </summary>
    public const string MailboxAddressesFieldName = "eMail Addresses";

    public async Task EnsureWellKnownMasksAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Folder, "Folder", [], cancellationToken);

        // A repository is a root document (ADR 0200). The mask carries no fields — like Folder, which it is a
        // copy of in everything but identity — and exists so a repository can SAY it is one (ADR 0627).
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Repository, "Repository", [], cancellationToken);

        // The personal space's general-purpose folder (#634). Fieldless and Folder-shaped; it exists so the
        // space's first level can admit by MASK rather than by name, which is the rule ADR 0633 settled.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.MyDocuments, "My Documents", [], cancellationToken);

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
        // "eMail Addresses" (#703): the addresses this mailbox receives for — a LIST of e-mail addresses, and
        // the first well-known list field, so it is also what exercises the IsList carry on the create and heal
        // paths. OPTIONAL is load-bearing twice: a mailbox without claims is valid (a personal mailbox derives
        // its address from its owner and needs no list), and the field-heal refuses required fields — optional
        // means every existing tenant's Mailbox mask gains it at next startup with no migration.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Mailbox, "Mailbox",
        [
            new FieldSpec(MailboxAddressesFieldName, FieldDataType.EmailAddress, IsRequired: false, IsList: true),
        ], cancellationToken);

        // Fieldless like the Mailbox it lives in — it types the folder as ephemeral (#596), and what is worth
        // indexing lives on the messages inside it.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.ImapSpecial, "IMAP Special", [], cancellationToken);

        // A user-created mail folder in the staging tier (#802) — fieldless, like the Section it is shaped
        // after. Its placement rows (under a staging folder or itself) come from ConstrainedPlacements in the
        // containment pass below.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.ImapFolder, "IMAP Folder", [], cancellationToken);

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
            // The picture's media type when the card carries one inline, absent otherwise. Indexed for the same
            // reason Repeats is on the Appointment mask: a listing that opened one .vcf per row merely to learn
            // whether there IS a photo would pay the per-row cost ADR 0557 forbids, and the answer is what
            // decides between drawing a face and drawing initials.
            new FieldSpec("Photo", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Appointment, "Appointment",
        [
            new FieldSpec("Event UID", FieldDataType.Text, IsRequired: true),
            // A concert at 19:00 and one at 21:00 are different appointments; a date cannot say so (#660).
            new FieldSpec("Start", FieldDataType.DateTime, IsRequired: false),
            new FieldSpec("End", FieldDataType.DateTime, IsRequired: false),
            new FieldSpec("Location", FieldDataType.Text, IsRequired: false),
            // The RRULE as stored, verbatim and opaque. Indexed rather than parsed at list time for the same
            // reason Start and Location are: a listing that opened each .ics to answer "does this repeat" would
            // read one blob per row, which is the per-row cost ADR 0557 exists to forbid. Optional, so the heal
            // adds it to tenants seeded before it existed; a document filed before it simply has no value, which
            // reads as "does not repeat" — right for every entry that does not, and corrected on its next write.
            new FieldSpec("Repeats", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        // AFTER the item, deliberately: MaskVersions has a unique (TenantId, Name) index, and this folder mask
        // is taking the name "Calendar" that the ITEM mask held before the rename to Appointment. Renaming the
        // item out of the way first is what stops the heal colliding with itself on every existing tenant.
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Calendar, "Calendar", [ColourField], cancellationToken);

        // After every mask exists, because containment is a relation BETWEEN masks: a Notebook's allowed parent
        // is the Mailbox, which is seeded eight lines below it, so doing this per-mask inside the loop above
        // would write a foreign key to a row that does not exist yet.
        await EnsureContainmentAsync(tenantId, cancellationToken);

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
            await EnsureAssignabilityAsync(tenantId, maskId, cancellationToken);
            return;
        }

        _dbContext.Masks.Add(new Mask
        {
            Id = maskId,
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsFolderMask = WellKnownMaskIds.FolderMasks.Contains(maskId),
            Icon = WellKnownMaskIds.IconTokens.GetValueOrDefault(maskId),
            UserCreatable = !WellKnownMaskIds.NotUserCreatable.Contains(maskId),
        });

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
                IsList = field.IsList,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await EnsureAssignabilityAsync(tenantId, maskId, cancellationToken);
    }

    /// <summary>
    /// Makes the mask say how it can be assigned: whether it types a folder, and which extensions claim it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on BOTH paths — the fresh create and the heal — for the reason #664 recorded: a fact added to the
    /// well-known masks reaches only tenants provisioned afterwards unless the heal carries it too, and the
    /// difference is invisible because both tenants look fine. Every existing tenant's masks were created
    /// before this column existed, so every one of them reads "not a folder mask" until this corrects it.
    /// </para>
    /// <para>
    /// Derived from <see cref="WellKnownMaskIds"/> rather than passed per call site: the folder/item partition
    /// is already stated there and is already guarded, so taking it from anywhere else would be a second copy
    /// of an answer that has to agree.
    /// </para>
    /// </remarks>
    private async Task EnsureAssignabilityAsync(Guid tenantId, Guid maskId, CancellationToken cancellationToken)
    {
        var mask = await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == maskId, cancellationToken);
        if (mask is null)
        {
            return;
        }

        var isFolderMask = WellKnownMaskIds.FolderMasks.Contains(maskId);
        if (mask.IsFolderMask != isFolderMask)
        {
            mask.IsFolderMask = isFolderMask;
        }

        // Healed rather than only seeded, because a tenant that exists already has NULL here and a
        // grow-only seed would leave it drawing generic folders forever — the #574 trap, which a
        // fresh-volume test cannot see because every tenant it creates is new.
        //
        // Assigned unconditionally to the shipped token, INCLUDING back to null for a mask that has none:
        // this is app-owned classification, so the well-known set is the authority. A tenant that wants a
        // different glyph changes the mask, not one of these.
        var icon = WellKnownMaskIds.IconTokens.GetValueOrDefault(maskId);
        if (mask.Icon != icon)
        {
            mask.Icon = icon;
        }

        // Healed for the same reason the icon is: a tenant that already exists was backfilled to `true` by the
        // migration, which would leave Repository, Mailbox and the rest offered on menus until this corrects
        // them. Assigned unconditionally, so a shipped mask cannot drift from what this release says it is.
        var userCreatable = !WellKnownMaskIds.NotUserCreatable.Contains(maskId);
        if (mask.UserCreatable != userCreatable)
        {
            mask.UserCreatable = userCreatable;
        }

        var wanted = WellKnownMaskIds.FileExtensions.TryGetValue(maskId, out var extensions)
            ? extensions
            : [];

        var existing = await _dbContext.MaskFileExtensions.IgnoreQueryFilters(["TenantFilter"])
            .Where(e => e.TenantId == tenantId && e.MaskId == maskId)
            .ToListAsync(cancellationToken);

        foreach (var extension in wanted.Where(w => !existing.Any(e => e.Extension == w)))
        {
            _dbContext.MaskFileExtensions.Add(new MaskFileExtension
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskId = maskId,
                Extension = extension,
            });
        }

        // Extensions this mask no longer claims are removed, so a mapping that MOVES between masks does not
        // leave the old row behind to violate the unique index the moment the new one is added.
        _dbContext.MaskFileExtensions.RemoveRange(existing.Where(e => !wanted.Contains(e.Extension)));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Writes typed-folder containment into the MODEL: where each well-known mask may live, what each folder
    /// admits, and the two one-directional flags (#673).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from <see cref="WellKnownMaskIds"/>' projections rather than restated, so the static tables and
    /// the rows remain one fact while both exist. The static tables are still what the invariant reads; this
    /// makes the database say the same thing, which is the step that has to be in place and CORRECT before
    /// enforcement can be moved onto it.
    /// </para>
    /// <para>
    /// Reconciles rather than appends: rows that are no longer wanted are removed, so a rule that MOVES between
    /// masks does not leave the old row behind to keep admitting something it should not. That is the direction
    /// that matters here — a stale containment row is permissive, and a permissive leftover is the one kind of
    /// drift nothing downstream reports.
    /// </para>
    /// <para>
    /// Ownership is bounded to rows whose BOTH ends are well-known. A tenant may one day declare that its own
    /// mask belongs in an Addressbook; that row is not this seed's to delete, and the reconcile must not treat
    /// "not in my table" as "wrong".
    /// </para>
    /// </remarks>
    private async Task EnsureContainmentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // A LIST, not the IReadOnlySet: EF translates Contains over IEnumerable/IList but not over
        // IReadOnlySet<T>, and the failure is a runtime translation exception rather than a compile error.
        var wellKnown = WellKnownMaskIds.All.ToList();

        var masks = await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"])
            .Where(m => m.TenantId == tenantId && wellKnown.Contains(m.Id))
            .ToListAsync(cancellationToken);

        foreach (var mask in masks)
        {
            // Every existing tenant's masks were created before these columns did, so all of them read
            // "unrestricted" until this corrects them — the permissive direction, and therefore the dangerous
            // one. #664's trap: a fresh-volume test cannot see this failing, because every tenant it makes is new.
            mask.AdmitsOnlyDeclaredChildren = WellKnownMaskIds.ExclusiveFolderMasks.Contains(mask.Id);
            mask.AdmitsNoSubfolders = WellKnownMaskIds.LeafFolderMasks.Contains(mask.Id);
        }

        var present = masks.Select(m => m.Id).ToHashSet();

        // A row may only be written once BOTH masks exist. They all do by now, but a tenant part-way through a
        // failed seed is a real state, and a foreign-key violation at startup would take the whole app down for
        // every tenant rather than leaving one incompletely healed.
        bool Both(Guid a, Guid b) => present.Contains(a) && present.Contains(b);

        var wantedParents = WellKnownMaskIds.AllowedParentMasks
            .SelectMany(pair => pair.Value.Select(parent => (MaskId: pair.Key, ParentMaskId: parent)))
            .Where(x => Both(x.MaskId, x.ParentMaskId))
            .ToHashSet();

        var existingParents = await _dbContext.MaskAllowedParents.IgnoreQueryFilters(["TenantFilter"])
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        foreach (var (maskId, parentMaskId) in wantedParents.Where(w =>
                     !existingParents.Any(e => e.MaskId == w.MaskId && e.ParentMaskId == w.ParentMaskId)))
        {
            _dbContext.MaskAllowedParents.Add(new MaskAllowedParent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskId = maskId,
                ParentMaskId = parentMaskId,
            });
        }

        _dbContext.MaskAllowedParents.RemoveRange(existingParents.Where(e =>
            WellKnownMaskIds.All.Contains(e.MaskId)
            && WellKnownMaskIds.All.Contains(e.ParentMaskId)
            && !wantedParents.Contains((e.MaskId, e.ParentMaskId))));

        var wantedChildren = WellKnownMaskIds.AdmittedChildMasks
            .SelectMany(pair => pair.Value.Select(child => (FolderMaskId: pair.Key, ChildMaskId: child)))
            .Where(x => Both(x.FolderMaskId, x.ChildMaskId))
            .ToHashSet();

        var existingChildren = await _dbContext.MaskAdmittedChildren.IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        foreach (var (folderMaskId, childMaskId) in wantedChildren.Where(w =>
                     !existingChildren.Any(e => e.FolderMaskId == w.FolderMaskId && e.ChildMaskId == w.ChildMaskId)))
        {
            _dbContext.MaskAdmittedChildren.Add(new MaskAdmittedChild
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FolderMaskId = folderMaskId,
                ChildMaskId = childMaskId,
            });
        }

        _dbContext.MaskAdmittedChildren.RemoveRange(existingChildren.Where(e =>
            WellKnownMaskIds.All.Contains(e.FolderMaskId)
            && WellKnownMaskIds.All.Contains(e.ChildMaskId)
            && !wantedChildren.Contains((e.FolderMaskId, e.ChildMaskId))));

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

        var defined = await _dbContext.FieldDefinitions.IgnoreQueryFilters(["TenantFilter"])
            .Where(f => f.TenantId == tenantId && f.MaskVersionId == currentVersion)
            .ToListAsync(cancellationToken);
        var existing = defined.Select(f => f.Name).ToList();

        // A field can also be present with the WRONG TYPE — `Start`/`End` on the Appointment mask were seeded
        // as Date and became DateTime (#660). This probe used to ask only "is the field there?", so a tenant
        // seeded before the change kept a date-only field forever while new tenants got the right one, and the
        // two behaved differently with nothing reporting it.
        //
        // Corrected IN PLACE, on the current version, rather than by minting a new one: OWNER-DECIDED
        // 2026-08-20, on the grounds that there is no user base yet to protect. That is a deliberate departure
        // from mask-version immutability (ADR 0166) and it is safe only while that premise holds — widening
        // Date → DateTime does not invalidate a stored value (every `yyyy-MM-dd` still parses), but a future
        // NARROWING would, and would need a real version.
        foreach (var field in defined)
        {
            if (fields.FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase)) is not { } spec)
            {
                continue;
            }

            if (field.DataType != spec.DataType)
            {
                field.DataType = spec.DataType;
            }

            // Multiplicity drifts exactly as the type does, and for the same reason (#703): every field
            // defined before IsList existed reads "single-valued", so a tenant seeded earlier would keep a
            // one-line editor for a field the app declares a list — and, worse, the API would refuse the
            // second value while a freshly provisioned tenant accepted it. Widening single → list cannot
            // invalidate a stored value; a future NARROWING could, and would need a real version.
            if (field.IsList != spec.IsList)
            {
                field.IsList = spec.IsList;
            }
        }

        var missing = fields.Where(f => !existing.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0)
        {
            // …but a type correction above may still be pending, and returning without saving is how a heal
            // silently does nothing on precisely the tenants that need it.
            await _dbContext.SaveChangesAsync(cancellationToken);
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
                IsList = field.IsList,
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
