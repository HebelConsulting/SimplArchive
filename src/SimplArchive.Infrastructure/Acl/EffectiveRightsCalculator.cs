using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Acl;

// See ADR "Effective rights computation", ADR "Document ACL inheritance resolution". A "repository-level"
// grant is now just a grant on a root Document (ParentId == null) — see ADR "Repository/Document
// unification", which removed the separate Repository-scope methods this class used to have (and the
// Repository.Status == Deactivated suspension check, since Repository no longer exists as a distinct
// lifecycle — a soft-deleted root document already 404s in every caller before its rights are checked,
// same as any other soft-deleted document). Group membership flows down the tree (ADR "User/group
// management model"): a user's effective group set is every group they're directly a member of, plus
// every descendant of each of those groups. Effective rights are the boolean union (OR) across every
// AclEntry matching the user directly or any group in that effective set, scoped to the governing Document.
public class EffectiveRightsCalculator : IEffectiveRightsCalculator
{
    private static readonly EffectiveRights TenantAdminRights = new(
        CanSee: true, CanReadContent: true, CanEditContent: true, CanEditIndexData: true,
        CanDelete: true, CanCreateSubItems: true, CanManagePermissions: true, CanMove: true, CanAnnotate: true);

    private static readonly EffectiveRights NoRights = new(
        CanSee: false, CanReadContent: false, CanEditContent: false, CanEditIndexData: false,
        CanDelete: false, CanCreateSubItems: false, CanManagePermissions: false, CanMove: false, CanAnnotate: false);

    private readonly SimplArchiveDbContext _dbContext;

    public EffectiveRightsCalculator(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Resolves ADR "Permissions / access control model"'s inherit-with-override: the target document's own
    // grants apply if it has BreaksInheritance = true; otherwise the walk continues up ParentId to the
    // nearest ancestor that does, ultimately falling back to the root document's own grants if no override
    // exists anywhere in the chain (a root document IS "the repository" now, so this is exactly the old
    // Repository-level fallback, just expressed as "the walk reached a document with no parent").
    public async Task<EffectiveRights> GetEffectiveRightsAsync(Guid userId, Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetEffectiveRightsForManyAsync(userId, [documentId], cancellationToken))[documentId];

    /// <summary>
    /// Exactly what <see cref="GetEffectiveRightsAsync"/> answers, for a whole page of documents at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because a listing needs a rights answer PER ROW to gate the destructive affordances (#858),
    /// and the per-document method costs roughly five constant queries plus one per ancestor level — so a
    /// 50-row page would have paid several hundred round trips on the hottest read in the app. That price is
    /// what turns a rule like "gate Delete on what the server allows" into something nobody implements.
    /// </para>
    /// <para>
    /// The saving comes from noticing which inputs are per-PRINCIPAL and which are per-DOCUMENT. The user row,
    /// the tenant row, the expanded group set, the group-conferred admin flag, the clearance ceiling and
    /// CanAccessWithoutGrant are all constant across the page and are read once. Only three things vary per
    /// document — where it sits in a personal space, its own sensitivity label, and its governing ACL scope —
    /// and each collapses into a single set-based query.
    /// </para>
    /// <para>
    /// The ancestor walk is done LEVEL BY LEVEL over the whole set rather than per document: every id still
    /// walking advances one parent per query, so the cost is the tree's DEPTH regardless of page size. A
    /// recursive CTE would be one query instead of a handful, and is deliberately not used — the model must
    /// stay provider-agnostic (PostgreSQL in production, SQLite in the integration tests), and a raw recursive
    /// query is exactly the provider-specific SQL that rules out.
    /// </para>
    /// <para>
    /// The ORDER of the checks is load-bearing and mirrors the single-document path exactly: tenant/user
    /// active first (ADRs 0174/0153 — neither bypass below may resurrect access for a deactivated holder),
    /// then the foreign-personal-space narrowing (ADR 0670), then the admin bypasses, then clearance, then the
    /// ACL match, with the CanAccessWithoutGrant top-up last. A batch that reordered them would be a second,
    /// subtly different answer to the same question, which is the whole reason the single-document method now
    /// delegates here instead of keeping its own copy.
    /// </para>
    /// <para>
    /// One consequence worth stating, because it is observable: a caller who bypasses as admin never reaches
    /// the walk, so a nonexistent document id returns admin rights rather than throwing — the same as before,
    /// since the old code returned before walking too.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, EffectiveRights>> GetEffectiveRightsForManyAsync(
        Guid userId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, EffectiveRights>();
        }

        var user = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.IsActive, u.IsTenantAdmin, u.ClearanceRank, u.CanAccessWithoutGrant })
            .SingleAsync(cancellationToken);

        var tenant = await _dbContext.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => new { t.Status, t.EnforceClearance })
            .SingleAsync(cancellationToken);

        // FIRST, and must stay first (ADRs 0174/0153).
        if (tenant.Status == TenantStatus.Deactivated || !user.IsActive)
        {
            return ids.ToDictionary(id => id, _ => NoRights);
        }

        // Per-document: whose personal space each id sits in (ADR 0670). One column read for the whole page.
        var personalRootOwners = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => ids.Contains(d.Id))
            .Select(d => new { d.Id, d.PersonalRootOwnerId })
            .ToDictionaryAsync(x => x.Id, x => x.PersonalRootOwnerId, cancellationToken);

        var effectiveGroupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);

        // Own IsTenantAdmin and a group-conferred one are the same total bypass, and both are per-principal —
        // so the group lookup happens once here rather than once per row.
        var isAdmin = user.IsTenantAdmin || await AnyGroupIsTenantAdminAsync(effectiveGroupIds, cancellationToken);

        var results = new Dictionary<Guid, EffectiveRights>(ids.Count);
        var remaining = new List<Guid>(ids.Count);

        foreach (var id in ids)
        {
            personalRootOwners.TryGetValue(id, out var owner);
            var insideForeignPersonalSpace = owner is { } ownerId && ownerId != userId;

            // The bypass is narrowed per document, which is exactly why this cannot be hoisted out of the loop.
            if (isAdmin && !insideForeignPersonalSpace)
            {
                results[id] = TenantAdminRights;
            }
            else
            {
                remaining.Add(id);
            }
        }

        if (remaining.Count == 0)
        {
            return results;
        }

        if (tenant.EnforceClearance)
        {
            var clearance = user.ClearanceRank;
            if (effectiveGroupIds.Count > 0)
            {
                var groupMax = await _dbContext.Groups
                    .Where(g => effectiveGroupIds.Contains(g.Id))
                    .Select(g => (int?)g.ClearanceRank)
                    .MaxAsync(cancellationToken) ?? 0;
                clearance = Math.Max(clearance, groupMax);
            }

            var blocked = await BlockedByClearanceAsync(remaining, clearance, cancellationToken);
            if (blocked.Count > 0)
            {
                foreach (var id in blocked)
                {
                    results[id] = NoRights;
                }

                remaining = [.. remaining.Where(id => !blocked.Contains(id))];
            }
        }

        if (remaining.Count == 0)
        {
            return results;
        }

        var governingScopes = await ResolveGoverningAclScopesAsync(remaining, cancellationToken);
        var distinctScopes = governingScopes.Values.Distinct().ToList();

        var matchingEntries = await _dbContext.AclEntries
            .Where(a => distinctScopes.Contains(a.DocumentId)
                && (a.UserId == userId || (a.GroupId.HasValue && effectiveGroupIds.Contains(a.GroupId.Value))))
            .ToListAsync(cancellationToken);

        var entriesByScope = matchingEntries
            .GroupBy(a => a.DocumentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Per-principal, so it is resolved at most once for the page — and only if some row actually lacks
        // CanSee, preserving the single-document path's "don't ask unless it matters".
        bool? holdsAccessWithoutGrant = null;

        foreach (var id in remaining)
        {
            var rights = BuildEffectiveRights(
                entriesByScope.TryGetValue(governingScopes[id], out var entries) ? entries : []);

            if (!rights.CanSee)
            {
                holdsAccessWithoutGrant ??=
                    await HoldsAccessWithoutGrantAsync(user.CanAccessWithoutGrant, effectiveGroupIds, cancellationToken);

                if (holdsAccessWithoutGrant.Value)
                {
                    rights = rights with { CanSee = true, CanReadContent = true };
                }
            }

            results[id] = rights;
        }

        return results;
    }

    // Deliberately conditioned on "lacks CanSee" rather than topping every right up: a caller granted CanSee
    // without CanReadContent keeps exactly that. A real grant is somebody's decision, and a blanket right that
    // quietly widened it would make grants unreadable.
    private async Task<bool> HoldsAccessWithoutGrantAsync(
        bool ownRight, HashSet<Guid> effectiveGroupIds, CancellationToken cancellationToken) =>
        ownRight
        || (effectiveGroupIds.Count > 0
            && await _dbContext.Groups.AnyAsync(
                g => effectiveGroupIds.Contains(g.Id) && g.CanAccessWithoutGrant, cancellationToken));

    // The owner of the personal space this document sits in, or null outside every personal space (ADR 0670).
    // IgnoreQueryFilters(["SoftDeleteFilter"]) mirrors the other reads here, so a rights check against an
    // already-soft-deleted document (restore, recycle bin) still resolves where it lives.
    private async Task<Guid?> PersonalRootOwnerOfAsync(Guid documentId, CancellationToken cancellationToken) =>
        await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == documentId)
            .Select(d => d.PersonalRootOwnerId)
            .FirstOrDefaultAsync(cancellationToken);

    // Clearance is about the document's OWN sensitivity label (Rank), not the inherited ACL scope — so this
    // reads the target document's label, not the governing document. Unlabelled (SensitivityLabelId == null) ⇒
    // rank 0 ⇒ never blocked. IgnoreQueryFilters(["SoftDeleteFilter"]) mirrors ResolveGoverningAclScopeAsync so
    // a rights check against an already-soft-deleted document (e.g. restore) still resolves the label.
    private async Task<bool> IsBlockedByClearanceAsync(Guid documentId, int effectiveClearance, CancellationToken cancellationToken) =>
        (await BlockedByClearanceAsync([documentId], effectiveClearance, cancellationToken)).Count > 0;

    // The same question for a whole page, in one query: which of these documents does this clearance not reach?
    // Unlabelled (SensitivityLabelId == null) never appears, which is what makes "absent" mean rank 0.
    private async Task<HashSet<Guid>> BlockedByClearanceAsync(
        IReadOnlyCollection<Guid> documentIds, int effectiveClearance, CancellationToken cancellationToken)
    {
        var ids = documentIds.ToList();

        var blocked = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => ids.Contains(d.Id) && d.SensitivityLabelId != null)
            .Join(_dbContext.SensitivityLabelDefinitions, d => d.SensitivityLabelId, l => l.Id, (d, l) => new { d.Id, l.Rank })
            .Where(x => x.Rank > effectiveClearance)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return [.. blocked];
    }

    private async Task<bool> AnyGroupIsTenantAdminAsync(HashSet<Guid> effectiveGroupIds, CancellationToken cancellationToken) =>
        effectiveGroupIds.Count > 0
        && await _dbContext.Groups.AnyAsync(g => effectiveGroupIds.Contains(g.Id) && g.IsTenantAdmin, cancellationToken);

    // Walks from documentId up via ParentId to the nearest ancestor (or the document itself) with
    // BreaksInheritance = true, returning that document's id as the governing scope; falls back to the
    // root document (ParentId == null) if the walk reaches it with no override found anywhere along the
    // way. One query per ancestor level (bounded by tree depth) rather than loading every document in the
    // tenant — unlike ExpandToDescendantsAsync below (which genuinely needs every group to expand
    // descendants), this walk only ever needs a single ancestor chain, so loading the whole tenant's
    // documents (no more RepositoryId to bound the load by, since Repository no longer exists — ADR
    // "Repository/Document unification") would load many unrelated documents for nothing. The walk
    // terminates safely without its own cycle guard, since Document cycles are already rejected at write
    // time (ADR "Document parent integrity and sibling name uniqueness"). IgnoreQueryFilters(["SoftDeleteFilter"])
    // only (tenant filter still applies) — cascade delete means a soft-deleted document's whole ancestor
    // chain is also soft-deleted, so the walk needs to see them too, or it throws for exactly the documents
    // a restore's own rights check needs to resolve — see ADR "Document delete/restore (recycle bin)
    // implementation".
    private async Task<Guid> ResolveGoverningAclScopeAsync(Guid documentId, CancellationToken cancellationToken) =>
        (await ResolveGoverningAclScopesAsync([documentId], cancellationToken))[documentId];

    /// <summary>The same walk for many documents at once — one query per tree LEVEL, not per document.</summary>
    /// <remarks>
    /// Every id still walking advances one parent per round, so a page of 50 siblings costs the same as one
    /// document at the same depth. Deliberately NOT a recursive CTE: the model stays provider-agnostic across
    /// PostgreSQL and SQLite, and raw recursive SQL is precisely what that rules out.
    ///
    /// Siblings converge on the same ancestor after one round, so the per-round id set collapses fast — which
    /// is also why the caller can group the ACL read by the DISTINCT scopes rather than per row.
    ///
    /// Terminates without a cycle guard for the same reason the single-document walk did: Document cycles are
    /// rejected at write time (ADR "Document parent integrity and sibling name uniqueness").
    /// </remarks>
    private async Task<Dictionary<Guid, Guid>> ResolveGoverningAclScopesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<Guid, Guid>(documentIds.Count);

        // original id -> where its walk currently stands.
        var walking = documentIds.Distinct().ToDictionary(id => id, id => id);

        while (walking.Count > 0)
        {
            var level = walking.Values.Distinct().ToList();

            var rows = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => level.Contains(d.Id))
                .Select(d => new { d.Id, d.ParentId, d.BreaksInheritance })
                .ToDictionaryAsync(d => d.Id, cancellationToken);

            var next = new Dictionary<Guid, Guid>();

            foreach (var (original, currentId) in walking)
            {
                if (!rows.TryGetValue(currentId, out var current))
                {
                    // The single-document walk used SingleAsync here, which threw for a missing row; keep that.
                    throw new InvalidOperationException(
                        $"Document {currentId} was not found while resolving the governing ACL scope of {original}.");
                }

                if (current.BreaksInheritance || current.ParentId is not { } parentId)
                {
                    resolved[original] = currentId;
                }
                else
                {
                    next[original] = parentId;
                }
            }

            walking = next;
        }

        return resolved;
    }

    // See ADR "ServiceAccount effective rights computation", ADR "ServiceAccount Document-scope effective
    // rights". No IsTenantAdmin-equivalent bypass (no such flag exists on ServiceAccount) and no group
    // expansion (GroupMembership.UserId is a real FK to User, not polymorphic, so a ServiceAccount cannot
    // belong to a Group at all) — just Tenant/ServiceAccount active checks, then a direct
    // AclEntry.ServiceAccountId match against the governing document.
    public async Task<EffectiveRights> GetEffectiveRightsForServiceAccountAsync(Guid serviceAccountId, Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetEffectiveRightsForManyForServiceAccountAsync(serviceAccountId, [documentId], cancellationToken))[documentId];

    /// <summary>The page-at-once form, for the same reason as the User one — a listing gates per row (#858).</summary>
    /// <remarks>
    /// Simpler than the User path in exactly the two ways the single-document version already was: a
    /// ServiceAccount cannot belong to a Group, so there is no expansion and no group-conferred bypass, and it
    /// has no IsTenantAdmin equivalent, so clearance always applies and nothing short-circuits ahead of it.
    /// That also means there is no per-document admin narrowing here, so every id walks.
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, EffectiveRights>> GetEffectiveRightsForManyForServiceAccountAsync(
        Guid serviceAccountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, EffectiveRights>();
        }

        var serviceAccount = await _dbContext.ServiceAccounts
            .Where(s => s.Id == serviceAccountId)
            .Select(s => new { s.TenantId, s.IsActive, s.ClearanceRank, s.CanAccessWithoutGrant })
            .SingleAsync(cancellationToken);

        var tenant = await _dbContext.Tenants
            .Where(t => t.Id == serviceAccount.TenantId)
            .Select(t => new { t.Status, t.EnforceClearance })
            .SingleAsync(cancellationToken);

        if (tenant.Status == TenantStatus.Deactivated || !serviceAccount.IsActive)
        {
            return ids.ToDictionary(id => id, _ => NoRights);
        }

        var results = new Dictionary<Guid, EffectiveRights>(ids.Count);
        var remaining = ids;

        if (tenant.EnforceClearance)
        {
            var blocked = await BlockedByClearanceAsync(remaining, serviceAccount.ClearanceRank, cancellationToken);
            if (blocked.Count > 0)
            {
                foreach (var id in blocked)
                {
                    results[id] = NoRights;
                }

                remaining = [.. remaining.Where(id => !blocked.Contains(id))];
            }
        }

        if (remaining.Count == 0)
        {
            return results;
        }

        var governingScopes = await ResolveGoverningAclScopesAsync(remaining, cancellationToken);
        var distinctScopes = governingScopes.Values.Distinct().ToList();

        var matchingEntries = await _dbContext.AclEntries
            .Where(a => distinctScopes.Contains(a.DocumentId) && a.ServiceAccountId == serviceAccountId)
            .ToListAsync(cancellationToken);

        var entriesByScope = matchingEntries
            .GroupBy(a => a.DocumentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var id in remaining)
        {
            var rights = BuildEffectiveRights(
                entriesByScope.TryGetValue(governingScopes[id], out var entries) ? entries : []);

            // CanAccessWithoutGrant (ADR 0670): its own column is the whole answer — no group union, because a
            // ServiceAccount cannot belong to a group.
            results[id] = rights.CanSee || !serviceAccount.CanAccessWithoutGrant
                ? rights
                : rights with { CanSee = true, CanReadContent = true };
        }

        return results;
    }

    private async Task<List<AclEntry>> GetMatchingEntriesForServiceAccountAsync(
        Guid serviceAccountId, Guid governingDocumentId, CancellationToken cancellationToken)
    {
        return await _dbContext.AclEntries
            .Where(a => a.DocumentId == governingDocumentId && a.ServiceAccountId == serviceAccountId)
            .ToListAsync(cancellationToken);
    }

    // Indexed-ACL (ADR "Indexed ACL in search"): the CanSee grantees on the document's governing scope, as
    // prefixed tokens. Groups are emitted as-granted (not expanded to members) — the caller is expanded to
    // their group set at query time instead, so a membership change never requires reindexing. A tenant
    // admin isn't represented here (they bypass the filter at query time). Empty ⇒ only admins can see it.
    public async Task<IReadOnlyCollection<string>> GetVisibilityPrincipalsAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var governingDocumentId = await ResolveGoverningAclScopeAsync(documentId, cancellationToken);

        var grantees = await _dbContext.AclEntries
            .Where(a => a.DocumentId == governingDocumentId && a.CanSee)
            .Select(a => new { a.UserId, a.GroupId, a.ServiceAccountId })
            .ToListAsync(cancellationToken);

        var tokens = new List<string>();
        foreach (var g in grantees)
        {
            if (g.UserId is { } userId)
            {
                tokens.Add(PrincipalToken.User(userId));
            }
            else if (g.GroupId is { } groupId)
            {
                tokens.Add(PrincipalToken.Group(groupId));
            }
            else if (g.ServiceAccountId is { } serviceAccountId)
            {
                tokens.Add(PrincipalToken.ServiceAccount(serviceAccountId));
            }
        }

        return tokens;
    }

    // Query-time side of indexed-ACL for a User: bypass (admin), no access (inactive user/tenant), or their
    // own token plus every group they effectively belong to (direct + descendants, membership flowing down —
    // the same expansion GetMatchingEntriesAsync uses).
    public async Task<SearchAccess> GetSearchAccessForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.IsActive, u.IsTenantAdmin, u.CanAccessWithoutGrant })
            .SingleAsync(cancellationToken);

        var tenantStatus = await _dbContext.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => t.Status)
            .SingleAsync(cancellationToken);

        if (tenantStatus == TenantStatus.Deactivated || !user.IsActive)
        {
            return SearchAccess.None;
        }

        var effectiveGroupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);
        var accessWithoutGrant = await HoldsAccessWithoutGrantAsync(user.CanAccessWithoutGrant, effectiveGroupIds, cancellationToken);

        // A group-conferred tenant admin bypasses the search ACL filter too (ADR "Enforce group system rights
        // for members"), same as an own IsTenantAdmin. Both are computed before the branch now, because
        // CanAccessWithoutGrant can confer the bypass on a caller who is no kind of admin at all (ADR 0670) —
        // an auditor who may read the archive without being able to touch it.
        var isAdmin = user.IsTenantAdmin || await AnyGroupIsTenantAdminAsync(effectiveGroupIds, cancellationToken);

        if (isAdmin || accessWithoutGrant)
        {
            // Personal spaces are restricted to the caller's own UNLESS they hold the right — which is what
            // makes a revoked admin's search honestly quiet rather than quietly complete.
            return new SearchAccess(
                BypassAcl: true, [], PersonalSpacesRestrictedTo: accessWithoutGrant ? null : userId);
        }

        var tokens = new List<string> { PrincipalToken.User(userId) };
        tokens.AddRange(effectiveGroupIds.Select(PrincipalToken.Group));
        return new SearchAccess(BypassAcl: false, tokens);
    }

    // Query-time side for a ServiceAccount: no admin bypass and no groups — just its own token, or no access
    // if it/its tenant is inactive.
    public async Task<SearchAccess> GetSearchAccessForServiceAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default)
    {
        var serviceAccount = await _dbContext.ServiceAccounts
            .Where(s => s.Id == serviceAccountId)
            .Select(s => new { s.TenantId, s.IsActive, s.CanAccessWithoutGrant })
            .SingleAsync(cancellationToken);

        var tenantStatus = await _dbContext.Tenants
            .Where(t => t.Id == serviceAccount.TenantId)
            .Select(t => t.Status)
            .SingleAsync(cancellationToken);

        if (tenantStatus == TenantStatus.Deactivated || !serviceAccount.IsActive)
        {
            return SearchAccess.None;
        }

        // No admin bypass exists here, but CanAccessWithoutGrant does (ADR 0670) — and it reads everywhere the
        // User path does, personal spaces included, since a ServiceAccount has no personal space of its own to
        // be "outside" of. Clearance still applies: the controller sets the ceiling from ClearanceScope, which
        // is unrestricted only for admins.
        return serviceAccount.CanAccessWithoutGrant
            ? new SearchAccess(BypassAcl: true, [])
            : new SearchAccess(BypassAcl: false, [PrincipalToken.ServiceAccount(serviceAccountId)]);
    }

    private static EffectiveRights BuildEffectiveRights(List<AclEntry> matchingEntries) => new(
        CanSee: matchingEntries.Any(a => a.CanSee),
        CanReadContent: matchingEntries.Any(a => a.CanReadContent),
        CanEditContent: matchingEntries.Any(a => a.CanEditContent),
        CanEditIndexData: matchingEntries.Any(a => a.CanEditIndexData),
        CanDelete: matchingEntries.Any(a => a.CanDelete),
        CanCreateSubItems: matchingEntries.Any(a => a.CanCreateSubItems),
        CanManagePermissions: matchingEntries.Any(a => a.CanManagePermissions),
        CanMove: matchingEntries.Any(a => a.CanMove),
        CanAnnotate: matchingEntries.Any(a => a.CanAnnotate));
}
