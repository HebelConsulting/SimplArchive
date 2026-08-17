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

    public SimplArchiveDbContext(DbContextOptions<SimplArchiveDbContext> options, ICurrentTenantAccessor currentTenantAccessor, IRealtimeNotifier? realtimeNotifier = null)
        : base(options)
    {
        _currentTenantAccessor = currentTenantAccessor;
        _realtimeNotifier = realtimeNotifier;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserProfilePhoto> UserProfilePhotos => Set<UserProfilePhoto>();

    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();

    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();

    public DbSet<SimplArchive.Domain.LegalHolds.LegalHold> LegalHolds => Set<SimplArchive.Domain.LegalHolds.LegalHold>();

    public DbSet<SimplArchive.Domain.Imap.ImapMailbox> ImapMailboxes => Set<SimplArchive.Domain.Imap.ImapMailbox>();

    public DbSet<SimplArchive.Domain.Imap.ImapMessageUid> ImapMessageUids => Set<SimplArchive.Domain.Imap.ImapMessageUid>();

    public DbSet<SimplArchive.Domain.Imap.ImapSeenMark> ImapSeenMarks => Set<SimplArchive.Domain.Imap.ImapSeenMark>();

    public DbSet<SimplArchive.Domain.CalDav.DavCollectionColor> DavCollectionColors => Set<SimplArchive.Domain.CalDav.DavCollectionColor>();

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
        }
    }

    // Typed-folder containment (#562 slice 5, generalized for #564's Contact/Calendar folders): a typed folder
    // admits ONLY children wearing its item mask, and such an item's PRIMARY location is only that folder —
    // references may point anywhere. The pairs are data (WellKnownMaskIds.TypedFolderPairs), so a new typed
    // family is a row there rather than another copy of this rule. Enforced here, the single enforcement point,
    // so every path (workbench move, import, WebDAV, IMAP, CalDAV/CardDAV) obeys.
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

        // A document whose type is not DETERMINED yet is exempt from the folder's admission rule: an upload
        // creates the row (and its Pending version) BEFORE the finalizer can read the bytes and classify it,
        // so enforcing here would refuse every .vcf/.ics before it could become a Contact/Calendar. Nothing
        // escapes through the gap — classification ends by assigning either the item mask (admitted) or Basic
        // Entry (a real mask, so the very next save is refused, which is exactly the rejection we want).
        var typeUndetermined = ownMaskId is null;

        foreach (var pair in Domain.Masks.WellKnownMaskIds.TypedFolderPairs)
        {
            if (!typeUndetermined && parentMaskId == pair.FolderMaskId && ownMaskId != pair.ItemMaskId)
            {
                throw new InvalidOperationException(
                    $"'{document.Name}' cannot live in a {pair.FolderName} — only {pair.ItemName}-masked documents can (typed-folder containment, #562/#564).");
            }

            if (ownMaskId == pair.ItemMaskId && parentMaskId != pair.FolderMaskId)
            {
                throw new InvalidOperationException(
                    $"'{document.Name}' wears the {pair.ItemName} mask and can only live in a {pair.FolderName} (typed-folder containment, #562/#564).");
            }
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

            ValidateFormatAndRange(fieldValue, fieldDefinition);
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

    private static void ValidateFormatAndRange(FieldValue fieldValue, FieldDefinition fieldDefinition)
    {
        switch (fieldDefinition.DataType)
        {
            case FieldDataType.Text:
                if (fieldDefinition.MaxTextLength is { } maxLength && fieldValue.Value.Length > maxLength)
                {
                    throw new InvalidOperationException(
                        $"Field value for '{fieldDefinition.Name}' exceeds the maximum length of {maxLength}.");
                }

                if (fieldDefinition.FormatPattern is { } pattern && !System.Text.RegularExpressions.Regex.IsMatch(fieldValue.Value, pattern))
                {
                    throw new InvalidOperationException(
                        $"Field value '{fieldValue.Value}' for '{fieldDefinition.Name}' does not match the required format.");
                }

                break;

            case FieldDataType.Number:
                var numberValue = decimal.Parse(fieldValue.Value, CultureInfo.InvariantCulture);

                if (fieldDefinition.MinValue is { } minNumberText
                    && numberValue < decimal.Parse(minNumberText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {numberValue} for '{fieldDefinition.Name}' is below the minimum of {minNumberText}.");
                }

                if (fieldDefinition.MaxValue is { } maxNumberText
                    && numberValue > decimal.Parse(maxNumberText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {numberValue} for '{fieldDefinition.Name}' is above the maximum of {maxNumberText}.");
                }

                break;

            case FieldDataType.Date:
                var dateValue = DateTimeOffset.Parse(fieldValue.Value, CultureInfo.InvariantCulture);

                if (fieldDefinition.MinValue is { } minDateText
                    && dateValue < DateTimeOffset.Parse(minDateText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {dateValue:O} for '{fieldDefinition.Name}' is before the minimum of {minDateText}.");
                }

                if (fieldDefinition.MaxValue is { } maxDateText
                    && dateValue > DateTimeOffset.Parse(maxDateText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {dateValue:O} for '{fieldDefinition.Name}' is after the maximum of {maxDateText}.");
                }

                break;

            case FieldDataType.Boolean:
            case FieldDataType.SingleSelect:
            case FieldDataType.MultiSelect:
                // No Format/Range constraints apply to these data types (ADR "Metadata field validation
                // rules" only defines Format for Text and Range for Number/Date).
                break;
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
