using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.PlatformAdministrators;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The solution's single EF Core DbContext (see ADR: Planned solution layout). Model configuration is
/// kept provider-agnostic (Fluent API only, no provider-specific column types or JSON columns) so it
/// behaves identically against PostgreSQL (production) and SQLite (tests) — see ADR: Testing / QA strategy.
/// </summary>
public class SimplArchiveDbContext : DbContext, IDataProtectionKeyContext
{
    // ASP.NET Core Data Protection keys, persisted in Postgres (ADR 0514) so antiforgery/auth cookies survive API
    // restarts and are shared across HPA replicas — the default ephemeral per-container key ring otherwise breaks
    // the first login after a restart and every login across replicas.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    private static readonly MethodInfo SetTenantQueryFilterMethod =
        typeof(SimplArchiveDbContext).GetMethod(nameof(SetTenantQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo SetConcurrencyTokenMethod =
        typeof(SimplArchiveDbContext).GetMethod(nameof(SetConcurrencyToken), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo SetSoftDeleteQueryFilterMethod =
        typeof(SimplArchiveDbContext).GetMethod(nameof(SetSoftDeleteQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    // Optional so the design-time factory + tests that construct the context directly (with no DI) still work;
    // DI injects the registered notifier (NullRealtimeNotifier, or the Api's SignalR broadcaster). Real-time
    // push (ADR "Real-time notifications (SignalR)") fires from the single SaveChangesAsync choke point below.
    private readonly IRealtimeNotifier? _realtimeNotifier;

    public SimplArchiveDbContext(
        DbContextOptions<SimplArchiveDbContext> options,
        ICurrentTenantAccessor currentTenantAccessor,
        IRealtimeNotifier? realtimeNotifier = null,
        IMaskContainmentProvider? containmentProvider = null)
        : base(options)
    {
        _currentTenantAccessor = currentTenantAccessor;
        _realtimeNotifier = realtimeNotifier;
        _containmentProvider = containmentProvider ?? new MaskContainmentProvider();
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    // The mail domains a tenant receives for (ADR 0628). Resolved BEFORE the tenant is known, so every
    // lookup here needs IgnoreQueryFilters(["TenantFilter"]).
    public DbSet<TenantMailDomain> TenantMailDomains => Set<TenantMailDomain>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserProfilePhoto> UserProfilePhotos => Set<UserProfilePhoto>();

    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();

    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();

    public DbSet<SimplArchive.Domain.LegalHolds.LegalHold> LegalHolds => Set<SimplArchive.Domain.LegalHolds.LegalHold>();

    public DbSet<SimplArchive.Domain.Imap.ImapMailbox> ImapMailboxes => Set<SimplArchive.Domain.Imap.ImapMailbox>();

    public DbSet<SimplArchive.Domain.Imap.ImapMessageUid> ImapMessageUids => Set<SimplArchive.Domain.Imap.ImapMessageUid>();

    public DbSet<SimplArchive.Domain.Imap.ImapSeenMark> ImapSeenMarks => Set<SimplArchive.Domain.Imap.ImapSeenMark>();

    public DbSet<SimplArchive.Domain.CalDav.DavCollectionColor> DavCollectionColors => Set<SimplArchive.Domain.CalDav.DavCollectionColor>();

    public DbSet<SimplArchive.Domain.CalDav.DavCollectionChange> DavCollectionChanges => Set<SimplArchive.Domain.CalDav.DavCollectionChange>();

    public DbSet<SimplArchive.Domain.CalDav.DavPushSubscription> DavPushSubscriptions => Set<SimplArchive.Domain.CalDav.DavPushSubscription>();

    public DbSet<SimplArchive.Domain.LegalHolds.LegalHoldItem> LegalHoldItems => Set<SimplArchive.Domain.LegalHolds.LegalHoldItem>();

    public DbSet<SimplArchive.Domain.Audit.AuditEvent> AuditEvents => Set<SimplArchive.Domain.Audit.AuditEvent>();

    public DbSet<SimplArchive.Domain.Notifications.Notification> Notifications => Set<SimplArchive.Domain.Notifications.Notification>();

    public DbSet<SimplArchive.Domain.Notifications.UserNotificationPreference> UserNotificationPreferences => Set<SimplArchive.Domain.Notifications.UserNotificationPreference>();

    public DbSet<SimplArchive.Domain.Search.SavedSearch> SavedSearches => Set<SimplArchive.Domain.Search.SavedSearch>();

    public DbSet<SimplArchive.Domain.Search.SavedSearchShare> SavedSearchShares => Set<SimplArchive.Domain.Search.SavedSearchShare>();

    public DbSet<SimplArchive.Domain.Documents.SensitivityLabelDefinition> SensitivityLabelDefinitions => Set<SimplArchive.Domain.Documents.SensitivityLabelDefinition>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();

    public DbSet<Mask> Masks => Set<Mask>();

    public DbSet<MaskFileExtension> MaskFileExtensions => Set<MaskFileExtension>();

    public DbSet<MaskAllowedParent> MaskAllowedParents => Set<MaskAllowedParent>();

    public DbSet<MaskAdmittedChild> MaskAdmittedChildren => Set<MaskAdmittedChild>();

    public DbSet<MaskVersion> MaskVersions => Set<MaskVersion>();

    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();

    public DbSet<FieldValue> FieldValues => Set<FieldValue>();

    public DbSet<AclEntry> AclEntries => Set<AclEntry>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<ChatMessageMention> ChatMessageMentions => Set<ChatMessageMention>();

    public DbSet<DocumentAnnotation> DocumentAnnotations => Set<DocumentAnnotation>();

    public DbSet<DocumentReference> DocumentReferences => Set<DocumentReference>();

    // Shares of a document with people who have no account (ADR 0546).
    public DbSet<ExternalLink> ExternalLinks => Set<ExternalLink>();

    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();

    public DbSet<TagDefinition> TagDefinitions => Set<TagDefinition>();

    public DbSet<DocumentSubscription> DocumentSubscriptions => Set<DocumentSubscription>();

    public DbSet<DocumentReminder> DocumentReminders => Set<DocumentReminder>();

    // Workflow (ADR "Workflow / document state model", 0009): the current approval state per version, plus the
    // append-only transition history.
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();

    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();

    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();

    // Deliberately not ITenantScoped — no tenant query filter applies here, since this principal exists
    // outside any tenant. See ADR "Tenant onboarding and platform-admin mechanism".
    public DbSet<PlatformAdministrator> PlatformAdministrators => Set<PlatformAdministrator>();

    // Durable async-indexing queue (ADR "Async indexing", 0011) — drained by SearchIndexWorker. Not
    // ITenantScoped (the worker spans every tenant).
    public DbSet<Search.SearchIndexOutbox> SearchIndexOutbox => Set<Search.SearchIndexOutbox>();

    // Durable searchable-PDF conversion queue (ADR "Searchable PDF successor for TIFFs") — drained by
    // SearchablePdfWorker. Not ITenantScoped (the worker spans every tenant).
    public DbSet<Conversion.SearchablePdfOutbox> SearchablePdfOutbox => Set<Conversion.SearchablePdfOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SimplArchiveDbContext).Assembly);

        // Registers OpenIddict's own entities (applications, authorizations, scopes, tokens) into this
        // same DbContext — see ADR: Planned authentication (OpenIddict is the sole token issuer) and
        // Planned solution layout (SimplArchive.Auth owns server *behavior* configuration; the actual
        // EF Core schema lives here, in the one solution-wide DbContext).
        modelBuilder.UseOpenIddict();

        // Every ITenantScoped entity added to the model automatically gets this filter — the single
        // tenant-isolation enforcement point (see ADR: Multi-tenancy resolution strategy). No entity
        // configuration needs to apply it manually.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                SetTenantQueryFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }

        // Every IConcurrencyTracked entity automatically gets ConcurrencyToken configured as an EF Core
        // concurrency token — see ADR: ETag / If-Match optimistic concurrency. No entity configuration
        // needs to apply it manually.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IConcurrencyTracked).IsAssignableFrom(entityType.ClrType))
            {
                SetConcurrencyTokenMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }

        // Every ISoftDeletable entity automatically gets this filter — the single soft-delete enforcement
        // point (see ADR: Document delete/restore (recycle bin) implementation). EF Core 10 combines
        // multiple HasQueryFilter calls on the same entity with AND, so this composes with the tenant
        // filter above without conflict.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                SetSoftDeleteQueryFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private void SetTenantQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        // Named, not the plain anonymous overload — EF Core throws at model-build time
        // ("Both anonymous and named query filters cannot be applied simultaneously") if an entity (e.g.
        // Document, which implements both ITenantScoped and ISoftDeletable) ends up with one anonymous
        // and one named filter. Both filters on this DbContext are named for that reason, verified via a
        // throwaway SQLite repro before this was applied here.
        modelBuilder.Entity<TEntity>().HasQueryFilter("TenantFilter", (TEntity e) => e.TenantId == _currentTenantAccessor.TenantId);
    }

    private void SetConcurrencyToken<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IConcurrencyTracked
    {
        modelBuilder.Entity<TEntity>().Property(e => e.ConcurrencyToken).IsConcurrencyToken();
    }

    private void SetSoftDeleteQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDeletable
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter("SoftDeleteFilter", (TEntity e) => e.DeletedAt == null);
    }

    public override int SaveChanges()
    {
        ValidateGroupsAsync(CancellationToken.None).GetAwaiter().GetResult();
        PersonalRootName.FollowDisplayNameAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        ValidateDocumentsAsync(CancellationToken.None).GetAwaiter().GetResult();
        ValidateFieldValuesAsync(CancellationToken.None).GetAwaiter().GetResult();
        ValidateRequiredFieldsAsync(CancellationToken.None).GetAwaiter().GetResult();
        PrepareMaskVersionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        RegenerateConcurrencyTokens();
        return base.SaveChanges();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await ValidateGroupsAsync(cancellationToken);
        await PersonalRootName.FollowDisplayNameAsync(this, cancellationToken);
        await ValidateDocumentsAsync(cancellationToken);
        await ValidateFieldValuesAsync(cancellationToken);
        await ValidateRequiredFieldsAsync(cancellationToken);
        await PrepareMaskVersionsAsync(cancellationToken);
        RegenerateConcurrencyTokens();

        // Snapshot the notifications being inserted BEFORE the save (so the state is still Added), then push them
        // live AFTER the commit — a single choke point covering every write path (ADR "Real-time notifications").
        var pushes = CollectNewNotifications();
        var saved = await base.SaveChangesAsync(cancellationToken);
        await PushRealtimeAsync(pushes, cancellationToken);
        return saved;
    }

    private List<(Guid UserId, RealtimeNotification Payload)> CollectNewNotifications()
    {
        if (_realtimeNotifier is null)
        {
            return [];
        }

        // Push a newly-inserted notification, and also a coalesced one — a Modified row whose EventCount changed
        // (ADR "Notification digest / coalescing"), so a digest bump refreshes the bell live. A mark-read/email
        // update (only ReadAt/EmailedAt modified, EventCount untouched) is deliberately not pushed.
        return ChangeTracker.Entries<SimplArchive.Domain.Notifications.Notification>()
            .Where(e => e.State == EntityState.Added
                || (e.State == EntityState.Modified && e.Property(n => n.EventCount).IsModified))
            .Select(e => (e.Entity.RecipientUserId, new RealtimeNotification(e.Entity.Title, e.Entity.Body)))
            .ToList();
    }

    // Best-effort: a push failure (no connections, transient hub error) must never break the mutation.
    private async Task PushRealtimeAsync(List<(Guid UserId, RealtimeNotification Payload)> pushes, CancellationToken cancellationToken)
    {
        if (_realtimeNotifier is null || pushes.Count == 0)
        {
            return;
        }

        foreach (var (userId, payload) in pushes)
        {
            try
            {
                await _realtimeNotifier.NotifyUserAsync(userId, payload, cancellationToken);
            }
            catch
            {
                // swallow — real-time delivery is best-effort; the notification is already persisted.
            }
        }
    }

    // Regenerates ConcurrencyToken to a fresh value for every Added/Modified IConcurrencyTracked entity —
    // see ADR: ETag / If-Match optimistic concurrency. Pure ChangeTracker inspection, no I/O, so unlike
    // the other maintenance methods this one is synchronous and doesn't need a cancellation token.
    private void RegenerateConcurrencyTokens()
    {
        foreach (var entry in ChangeTracker.Entries<IConcurrencyTracked>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ConcurrencyToken = Guid.NewGuid();
            }
        }
    }

    // Single enforcement point for three invariants about Group: a group cannot contain itself, directly
    // or transitively (see ADR: Group cycle detection mechanism); a group's parent must belong to the same
    // tenant (see ADR: Cross-tenant group parent enforcement); and sibling groups (same tenant + parent,
    // including root-level groups sharing a null parent) can't share a name (see ADR: Group name uniqueness
    // scope). Every write path goes through SaveChanges regardless of which handler triggered it, so none
    // of these checks can be bypassed the way a per-handler check could be.
    private async Task ValidateGroupsAsync(CancellationToken cancellationToken)
    {
        var changedGroups = ChangeTracker.Entries<Group>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        if (changedGroups.Count == 0)
        {
            return;
        }

        var trackedGroups = ChangeTracker.Entries<Group>().ToDictionary(e => e.Entity.Id, e => e.Entity);

        foreach (var group in changedGroups)
        {
            if (group.ParentGroupId.HasValue)
            {
                await DetectCycleAndCrossTenantParentAsync(group, trackedGroups, cancellationToken);
            }

            await EnsureUniqueSiblingNameAsync(group, trackedGroups.Values, cancellationToken);
        }
    }

    private async Task DetectCycleAndCrossTenantParentAsync(
        Group group, Dictionary<Guid, Group> trackedGroups, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { group.Id };
        var currentId = group.ParentGroupId;

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
            {
                throw new InvalidOperationException(
                    $"Group '{group.Id}' cannot be its own ancestor — assigning parent '{group.ParentGroupId}' would create a cycle.");
            }

            Guid parentTenantId;
            Guid? parentId;

            if (trackedGroups.TryGetValue(currentId.Value, out var trackedParent))
            {
                parentTenantId = trackedParent.TenantId;
                parentId = trackedParent.ParentGroupId;
            }
            else
            {
                // Ignores the tenant query filter deliberately, so a cross-tenant parent is caught by
                // the explicit check below with a clear message, rather than failing opaquely because
                // the filtered query found no matching row.
                var parent = await Groups
                    .IgnoreQueryFilters()
                    .Where(g => g.Id == currentId.Value)
                    .Select(g => new { g.TenantId, g.ParentGroupId })
                    .SingleAsync(cancellationToken);
                parentTenantId = parent.TenantId;
                parentId = parent.ParentGroupId;
            }

            if (parentTenantId != group.TenantId)
            {
                throw new InvalidOperationException(
                    $"Group '{group.Id}' (tenant '{group.TenantId}') cannot have a parent belonging to a different tenant ('{parentTenantId}').");
            }

            currentId = parentId;
        }
    }

    private async Task EnsureUniqueSiblingNameAsync(
        Group group, IEnumerable<Group> trackedGroups, CancellationToken cancellationToken)
    {
        var conflictsWithinBatch = trackedGroups.Any(other =>
            other.Id != group.Id
            && other.TenantId == group.TenantId
            && other.ParentGroupId == group.ParentGroupId
            && other.Name == group.Name);

        if (conflictsWithinBatch)
        {
            throw new InvalidOperationException(
                $"Group '{group.Id}' cannot share the name '{group.Name}' with another group under the same parent.");
        }

        // Nullable-vs-nullable equality here (g.ParentGroupId == group.ParentGroupId) is translated by EF
        // Core as a null-safe comparison (true when both sides are null), unlike a raw SQL "=" operator —
        // this is exactly why this check lives here rather than in a database unique index, which would
        // treat every NULL ParentGroupId as distinct and silently miss root-level name collisions.
        var conflictsWithPersisted = await Groups
            .Where(g => g.Id != group.Id
                && g.TenantId == group.TenantId
                && g.ParentGroupId == group.ParentGroupId
                && g.Name == group.Name)
            .AnyAsync(cancellationToken);

        if (conflictsWithPersisted)
        {
            throw new InvalidOperationException(
                $"Group '{group.Id}' cannot share the name '{group.Name}' with another group under the same parent.");
        }
    }

    // Single enforcement point for two invariants about Document, mirroring the Group precedent exactly
    // (see ADR: Document parent integrity and sibling name uniqueness): a document cannot contain itself,
    // directly or transitively; and sibling documents (same tenant + parent, including root-level
    // documents sharing a null parent) can't share a name. A "repository" is now just a Document with
    // ParentId == null (ADR "Repository/Document unification"), so root-level name uniqueness here is
    // exactly what used to be Repository.Name's own tenant-wide uniqueness — one simpler rule reproduces
    // both old behaviors. Every write path goes through SaveChanges regardless of which handler triggered
    // it, so none of these checks can be bypassed the way a per-handler check could be.
    private async Task ValidateDocumentsAsync(CancellationToken cancellationToken)
    {
        var changedDocuments = ChangeTracker.Entries<Document>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        if (changedDocuments.Count == 0)
        {
            return;
        }

        var trackedDocuments = ChangeTracker.Entries<Document>().ToDictionary(e => e.Entity.Id, e => e.Entity);

        foreach (var document in changedDocuments)
        {
            if (document.ParentId.HasValue)
            {
                await DetectDocumentCycleAndCrossTenantParentAsync(document, trackedDocuments, cancellationToken);
            }

            await EnsureUniqueSiblingDocumentNameAsync(document, trackedDocuments.Values, cancellationToken);
            await EnforceTypedFolderContainmentAsync(document, cancellationToken);
            await DocumentMaskInvariants.EnforceAsync(this, document, cancellationToken);
            await EnforcePersonalSpaceStructureAsync(document, cancellationToken);
            await PersonalRootOwner.MaintainAsync(this, document, trackedDocuments, cancellationToken);
        }
    }

    // The personal space's first level is CLOSED (#596): the four provisioned folders cannot be deleted or
    // moved out, and the only thing a user may add beside them is a plain Folder.
    //
    // Both halves live here for the same reason as every other document invariant: the personal space is
    // written by the workbench, WebDAV, CalDAV/CardDAV, IMAP, LMTP, import and move, and a check in any one of
    // them is a check the other six skip. The protection half is the one with teeth today — nothing else stops
    // a user deleting the calendar a CalDAV client is subscribed to.
    private async Task EnforcePersonalSpaceStructureAsync(Document document, CancellationToken cancellationToken)
    {
        var entry = Entry(document);

        // A protected folder may not LEAVE the personal space. Checked against the ORIGINAL parent, because by
        // the time this runs the document already claims its new one.
        var originalParentId = entry.State == EntityState.Modified
            ? entry.Property(d => d.ParentId).OriginalValue
            : document.ParentId;

        if (PersonalFolders.IsProtected(document.Name)
            && originalParentId is { } from
            && await IsPersonalRootAsync(from, cancellationToken))
        {
            if (entry.Property(d => d.ParentId).IsModified && document.ParentId != from)
            {
                throw PersonalSpaceStructureException.CannotMove(document.Name);
            }

            // Soft delete is the deletion that matters here: a folder in the recycle bin is just as absent
            // from the tree, and just as gone from a subscribed client's point of view.
            if (entry.Property(d => d.DeletedAt).IsModified && document.DeletedAt is not null)
            {
                throw PersonalSpaceStructureException.CannotDelete(document.Name);
            }
        }

        // Admission applies when something ARRIVES at the first level — created there, or moved there — and not
        // on every later edit of what is already sitting there. That distinction is load-bearing rather than
        // tidy: a pre-upgrade personal space holds MASKLESS folders waiting to be healed, and a rule that
        // re-validated them on modification would refuse the very writes that fix them, in exactly the
        // deployments the heal exists for.
        var arriving = entry.State == EntityState.Added
                       || (entry.Property(d => d.ParentId).IsModified
                           && entry.Property(d => d.ParentId).OriginalValue != document.ParentId);

        // …and when the MASK is assigned to something already sitting here (#644). Arrival-gating alone left a
        // bypass: maskless is admitted (it is the pre-upgrade state), so anything created maskless and masked
        // AFTERWARDS walked straight past this rule. That is not theoretical — a file dropped on the mounted
        // `Personal` drive over WebDAV took exactly that route until its create learned to stamp a mask.
        //
        // It does NOT re-break the heal, which is what arrival-gating was protecting: a heal assigns an
        // ADMITTED mask (My Documents, Calendar, Addressbook, Mailbox), so it passes the check below rather
        // than being exempted from it. Only an assignment the level would have refused on arrival is refused
        // here — which is the same question, asked at the moment the answer becomes knowable.
        var masked = entry.State == EntityState.Modified
                     && entry.Property(d => d.MaskVersionId).IsModified
                     && document.MaskVersionId is not null;

        if (!arriving && !masked)
        {
            return;
        }

        if (document.ParentId is not { } parentId
            || !await IsPersonalRootAsync(parentId, cancellationToken))
        {
            return;
        }

        // Admission is decided by MASK, not by name. Deciding it by name looked equivalent and is not: a
        // provisioned folder caught mid-rename still wears its mask but not yet its new name, so a name-based
        // rule refuses the very migration that renames it — which is how this was found.
        // A MASKLESS document is admitted. It is not "an unknown kind" but the pre-upgrade state: a tenant
        // provisioned before a mask existed gets its folders with no mask at all, and the provisioner itself
        // creates them that way when the seed has not run. Refusing them makes provisioning fail on precisely
        // the deployments the mask-heal exists to repair — found by the heal test, not by reasoning.
        //
        // The consequence is that an unclassified upload is admitted here too, so "no loose files in the
        // personal space" is NOT yet enforced (#596 leaves that question open).
        var admitted = document.MaskVersionId is not { } maskVersionId
            || await MaskVersions.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(
                    v => v.Id == maskVersionId && PersonalFolders.FirstLevelMasks.Contains(v.MaskId),
                    cancellationToken);

        if (!admitted)
        {
            throw PersonalSpaceStructureException.NotAdmitted(document.Name);
        }
    }

    private async Task<bool> IsPersonalRootAsync(Guid documentId, CancellationToken cancellationToken) =>
        await Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .AnyAsync(d => d.Id == documentId && d.PersonalOfUserId != null, cancellationToken);

    // Typed-folder containment (#562 slice 5, generalized for #564's Contact/Calendar folders and its notebook
    // sections): a typed folder admits ONLY children wearing one of its admitted masks, and such a child's
    // PRIMARY location is only a folder that admits it — references may point anywhere. Enforced here, the
    // single enforcement point, so every path (workbench move, import, WebDAV, IMAP, CalDAV/CardDAV) obeys.
    //
    // The rules are read from the MODEL since ADR 0655 — MaskContainmentRules.Verify decides, and it is pure,
    // so the decision itself is testable without a database. What stays here is the part that needs one: which
    // masks the two documents wear, and whether the folder has room for another.
    private async Task EnforceTypedFolderContainmentAsync(Document document, CancellationToken cancellationToken)
    {
        async Task<Guid?> MaskIdOfAsync(Guid? maskVersionId) => maskVersionId is not { } mv
            ? null
            : await MaskVersions.IgnoreQueryFilters()
                .Where(v => v.Id == mv)
                .Select(v => (Guid?)v.MaskId)
                .SingleOrDefaultAsync(cancellationToken);

        var ownMaskId = await MaskIdOfAsync(document.MaskVersionId);
        Guid? parentMaskId = null;
        if (document.ParentId is { } parentId)
        {
            var parentMaskVersionId = ChangeTracker.Entries<Document>().FirstOrDefault(e => e.Entity.Id == parentId)?.Entity.MaskVersionId
                ?? await Documents.IgnoreQueryFilters().Where(d => d.Id == parentId).Select(d => d.MaskVersionId).SingleOrDefaultAsync(cancellationToken);
            parentMaskId = await MaskIdOfAsync(parentMaskVersionId);
        }

        (await _containmentProvider.ForAsync(this, document.TenantId, cancellationToken))
            .Verify(document.Name, ownMaskId, parentMaskId);

        // …and does the folder have ROOM for it? A separate question from admission, and the one that stays
        // STATIC: the folder here admits anything, and the only limit is how many of ONE mask it holds (a
        // personal space, one mailbox). Answering it means counting siblings, which is a query and not a rule.
        if (ownMaskId is { } countedId
            && document.ParentId is { } folderId
            && Domain.Masks.WellKnownMaskIds.ChildCardinalityRules
                .FirstOrDefault(r => r.FolderMaskId == parentMaskId && r.ChildMaskId == countedId) is { } capacityRule)
        {
            await EnforceChildCardinalityAsync(document, folderId, countedId, capacityRule, cancellationToken);
        }
    }

    // Loaded once per request through the SHARED provider, so the rules this invariant enforces are the same
    // object the Api offers actions from — an offer the invariant refuses is an action that fails on click, and
    // a withheld offer hides one that would have worked. One load also means a bulk move of 500 documents pays
    // for the rules once rather than 500 times.
    //
    // The fallback is for the many tests (and the design-time factory) that construct this context with no DI:
    // they get a private provider, which is the same per-instance cache this had before the Api needed to share
    // it. A null provider must never mean "no rules" — that would read as unrestricted, the permissive
    // direction, and silently disable containment in exactly the setups that exercise it hardest.
    private readonly IMaskContainmentProvider _containmentProvider;

    // Counted at the same point as every other document invariant, which is what makes a RESTORE safe: clearing
    // DeletedAt is a save like any other, so a mailbox coming back out of the recycle bin beside a replacement
    // is refused here rather than needing a rule of its own on the restore path.
    private async Task EnforceChildCardinalityAsync(
        Document document,
        Guid folderId,
        Guid childMaskId,
        Domain.Masks.ChildCardinalityRule rule,
        CancellationToken cancellationToken)
    {
        var maskVersionIds = await MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == document.TenantId && v.MaskId == childMaskId)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        // Counting the tracked entries and the persisted rows SEPARATELY and adding them is wrong twice over: a
        // sibling that is merely touched is in both and counts twice, and one being moved OUT in this same save
        // is still in the database and counts as occupying a slot it is leaving. So collect IDS and let the set
        // resolve the overlap. (The sibling-name check gets away with two independent queries because it asks
        // for ANY rather than HOW MANY, and a double-counted boolean is still that boolean.)
        //
        // Soft-deleted siblings are excluded by the SoftDeleteFilter — the "only live ones count" reading, so
        // deleting a mailbox frees the slot at once, exactly as deleting a document frees its name.
        var occupants = (await Documents
            .Where(d => d.Id != document.Id
                && d.ParentId == folderId
                && d.MaskVersionId != null
                && maskVersionIds.Contains(d.MaskVersionId.Value))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        // The change tracker holds the INTENDED state, so it overrides the database in both directions: a
        // sibling arriving in this save is added (two mailboxes created at once would otherwise each see none
        // of the other), and one leaving, being deleted, or being restamped is removed.
        foreach (var tracked in ChangeTracker.Entries<Document>()
            .Where(e => e.State != EntityState.Detached)
            .Select(e => e.Entity)
            .Where(other => other.Id != document.Id))
        {
            var occupies = tracked.ParentId == folderId
                && tracked.DeletedAt == null
                && tracked.MaskVersionId is { } mv
                && maskVersionIds.Contains(mv);

            if (occupies)
            {
                occupants.Add(tracked.Id);
            }
            else
            {
                occupants.Remove(tracked.Id);
            }
        }

        if (occupants.Count >= rule.Max)
        {
            throw Domain.Masks.TypedFolderContainmentException.FolderAlreadyHolds(document.Name, rule);
        }
    }

    private async Task DetectDocumentCycleAndCrossTenantParentAsync(
        Document document, Dictionary<Guid, Document> trackedDocuments, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { document.Id };
        var currentId = document.ParentId;

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
            {
                throw new InvalidOperationException(
                    $"Document '{document.Id}' cannot be its own ancestor — assigning parent '{document.ParentId}' would create a cycle.");
            }

            Guid parentTenantId;
            Guid? parentId;

            if (trackedDocuments.TryGetValue(currentId.Value, out var trackedParent))
            {
                parentTenantId = trackedParent.TenantId;
                parentId = trackedParent.ParentId;
            }
            else
            {
                // Ignores the tenant query filter deliberately, so a cross-tenant parent is caught by
                // the explicit check below with a clear message, rather than failing opaquely because
                // the filtered query found no matching row.
                var parent = await Documents
                    .IgnoreQueryFilters()
                    .Where(d => d.Id == currentId.Value)
                    .Select(d => new { d.TenantId, d.ParentId })
                    .SingleAsync(cancellationToken);
                parentTenantId = parent.TenantId;
                parentId = parent.ParentId;
            }

            if (parentTenantId != document.TenantId)
            {
                throw new InvalidOperationException(
                    $"Document '{document.Id}' (tenant '{document.TenantId}') cannot have a parent belonging to a different tenant ('{parentTenantId}').");
            }

            currentId = parentId;
        }
    }

    private async Task EnsureUniqueSiblingDocumentNameAsync(
        Document document, IEnumerable<Document> trackedDocuments, CancellationToken cancellationToken)
    {
        // Personal repositories (ADR "Per-user personal repository") live in a per-user namespace enforced by the
        // partial unique index on (TenantId, PersonalOfUserId) — every user's is named "Personal", so they must
        // be exempt from the tenant-wide root sibling-name rule (and excluded from the conflict queries below so
        // they never block an ordinary root document of the same name either).
        if (document.PersonalOfUserId.HasValue)
        {
            return;
        }

        var conflictsWithinBatch = trackedDocuments.Any(other =>
            other.Id != document.Id
            && other.PersonalOfUserId == null
            && other.TenantId == document.TenantId
            && other.ParentId == document.ParentId
            && other.Name == document.Name);

        if (conflictsWithinBatch)
        {
            throw new InvalidOperationException(
                $"Document '{document.Id}' cannot share the name '{document.Name}' with another document under the same parent.");
        }

        // Nullable-vs-nullable equality here (d.ParentId == document.ParentId) is translated by EF Core as
        // a null-safe comparison (true when both sides are null), unlike a raw SQL "=" operator — this is
        // exactly why this check lives here rather than in a database unique index, which would treat
        // every NULL ParentId as distinct and silently miss root-level name collisions.
        var conflictsWithPersisted = await Documents
            .Where(d => d.Id != document.Id
                && d.PersonalOfUserId == null
                && d.TenantId == document.TenantId
                && d.ParentId == document.ParentId
                && d.Name == document.Name)
            .AnyAsync(cancellationToken);

        if (conflictsWithPersisted)
        {
            throw new InvalidOperationException(
                $"Document '{document.Id}' cannot share the name '{document.Name}' with another document under the same parent.");
        }
    }

    // Enforcement point for the per-field Format and Range constraints (see ADR: Metadata field validation
    // rules — the Unique constraint that used to also live here was removed entirely, see ADR
    // "Repository/Document unification": field metadata doesn't need cross-document uniqueness). Required
    // is checked separately, in ValidateRequiredFieldsAsync below — see ADR: Required field validation
    // trigger for why it fires on Document.MaskVersionId assignment rather than on every FieldValue write.
    private async Task ValidateFieldValuesAsync(CancellationToken cancellationToken)
    {
        var changedFieldValues = ChangeTracker.Entries<FieldValue>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        if (changedFieldValues.Count == 0)
        {
            return;
        }

        var trackedFieldDefinitions = ChangeTracker.Entries<FieldDefinition>().ToDictionary(e => e.Entity.Id, e => e.Entity);

        foreach (var fieldValue in changedFieldValues)
        {
            if (!trackedFieldDefinitions.TryGetValue(fieldValue.FieldDefinitionId, out var fieldDefinition))
            {
                fieldDefinition = await FieldDefinitions.SingleAsync(f => f.Id == fieldValue.FieldDefinitionId, cancellationToken);
            }

            FieldValueValidation.EnsureValid(fieldValue, fieldDefinition);
        }
    }

    // Enforcement point for the Required constraint (see ADR: Metadata field validation rules, ADR:
    // Required field validation trigger) — fires only when a Document's MaskVersionId is assigned or
    // reassigned in this batch, not on every FieldValue write, so a document can still be created and
    // have its metadata filled in incrementally before a mask is attached.
    private async Task ValidateRequiredFieldsAsync(CancellationToken cancellationToken)
    {
        var changedDocuments = ChangeTracker.Entries<Document>()
            .Where(e => e.State == EntityState.Added
                || (e.State == EntityState.Modified && e.Property(d => d.MaskVersionId).IsModified))
            .Select(e => e.Entity)
            .Where(d => d.MaskVersionId is not null)
            .ToList();

        if (changedDocuments.Count == 0)
        {
            return;
        }

        var trackedFieldValues = ChangeTracker.Entries<FieldValue>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .ToList();

        foreach (var document in changedDocuments)
        {
            var maskVersionId = document.MaskVersionId!.Value;

            var requiredFieldDefinitions = await FieldDefinitions
                .Where(f => f.MaskVersionId == maskVersionId && f.IsRequired)
                .ToListAsync(cancellationToken);

            if (requiredFieldDefinitions.Count == 0)
            {
                continue;
            }

            var persistedFieldIds = await FieldValues
                .Where(v => v.DocumentId == document.Id)
                .Select(v => v.FieldDefinitionId)
                .ToListAsync(cancellationToken);

            var presentFieldIds = persistedFieldIds
                .Concat(trackedFieldValues.Where(v => v.DocumentId == document.Id).Select(v => v.FieldDefinitionId))
                .ToHashSet();

            var missingFieldDefinition = requiredFieldDefinitions.FirstOrDefault(f => !presentFieldIds.Contains(f.Id));

            if (missingFieldDefinition is not null)
            {
                throw new InvalidOperationException(
                    $"Document '{document.Id}' is missing a value for required field '{missingFieldDefinition.Name}'.");
            }
        }
    }

    // Maintains VersionNumber and IsCurrent for newly added MaskVersions — see ADR "Mask versioning data
    // shape" and ADR "Mask name uniqueness across versions". Every added version becomes the new current
    // one for its Mask; the previously-current version (if any) is flipped off in the same save, which is
    // what lets the partial unique index on (TenantId, Name) WHERE IsCurrent = true actually hold.
    private async Task PrepareMaskVersionsAsync(CancellationToken cancellationToken)
    {
        var newVersions = ChangeTracker.Entries<MaskVersion>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        if (newVersions.Count == 0)
        {
            return;
        }

        foreach (var newVersion in newVersions)
        {
            // TenantId must be part of every one of these comparisons, not just MaskId — see ADR "Mask
            // composite primary key for cross-tenant well-known IDs": the same MaskId can now belong to
            // multiple different tenants (the 3 well-known masks), and unlike the MaskVersions DbSet
            // queries below (which get the tenant filter automatically), ChangeTracker.Entries<T>()
            // enumerates in-memory tracked entities directly, bypassing query filters entirely.
            var trackedSiblings = ChangeTracker.Entries<MaskVersion>()
                .Select(e => e.Entity)
                .Where(v => v.Id != newVersion.Id && v.TenantId == newVersion.TenantId && v.MaskId == newVersion.MaskId)
                .ToList();

            var persistedMaxVersionNumber = await MaskVersions
                .Where(v => v.TenantId == newVersion.TenantId && v.MaskId == newVersion.MaskId)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(cancellationToken) ?? 0;

            var maxVersionNumber = Math.Max(persistedMaxVersionNumber, trackedSiblings.Select(v => v.VersionNumber).DefaultIfEmpty(0).Max());

            newVersion.VersionNumber = maxVersionNumber + 1;
            newVersion.IsCurrent = true;

            foreach (var sibling in trackedSiblings.Where(v => v.IsCurrent))
            {
                sibling.IsCurrent = false;
            }

            var persistedCurrentVersion = await MaskVersions
                .Where(v => v.TenantId == newVersion.TenantId && v.MaskId == newVersion.MaskId && v.IsCurrent)
                .SingleOrDefaultAsync(cancellationToken);

            if (persistedCurrentVersion is not null && !trackedSiblings.Any(v => v.Id == persistedCurrentVersion.Id))
            {
                persistedCurrentVersion.IsCurrent = false;
            }
        }
    }
}
