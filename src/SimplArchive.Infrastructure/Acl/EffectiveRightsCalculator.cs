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
    public async Task<EffectiveRights> GetEffectiveRightsAsync(Guid userId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId, u.IsActive, u.IsTenantAdmin, u.ClearanceRank, u.CanAccessWithoutGrant })
            .SingleAsync(cancellationToken);

        var tenant = await _dbContext.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => new { t.Status, t.EnforceClearance })
            .SingleAsync(cancellationToken);

        // Tenant/User active checks and IsTenantAdmin bypass are identical regardless of the document — see ADR
        // "Tenant deactivation cascade to users", ADR "EffectiveRightsCalculator vs deactivated users", ADR
        // "Tenant admin ACL bypass". A tenant admin also bypasses clearance (ADR "Sensitivity clearance
        // enforcement"), which is why the clearance check below only runs on the non-admin path.
        //
        // This runs FIRST and must stay first (ADRs 0174/0153): neither the bypass below nor
        // CanAccessWithoutGrant may resurrect access for a deactivated holder.
        if (tenant.Status == TenantStatus.Deactivated || !user.IsActive)
        {
            return NoRights;
        }

        // ...but the bypass is no longer unconditional: inside SOMEBODY ELSE'S personal space an admin is an
        // ordinary caller in every respect — no ACL bypass and no clearance bypass (ADR 0670). What they keep
        // there is CanAccessWithoutGrant, which promotion grants and which they can revoke from themselves.
        // One column read rather than a walk to the root; see Document.PersonalRootOwnerId.
        var personalRootOwnerId = await PersonalRootOwnerOfAsync(documentId, cancellationToken);
        var insideForeignPersonalSpace = personalRootOwnerId is { } owner && owner != userId;

        if (user.IsTenantAdmin && !insideForeignPersonalSpace)
        {
            return TenantAdminRights;
        }

        var effectiveGroupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);

        // A group flagged IsTenantAdmin confers the same total ACL bypass (and clearance bypass) on its members
        // as an own IsTenantAdmin — see ADR "Enforce group system rights for members". Checked here (after the
        // own-admin short-circuit) so the expanded group set is computed once and reused for the clearance
        // ceiling and the AclEntry match below. Narrowed identically: a group-conferred admin is no more
        // entitled to read somebody's personal space than a directly-flagged one.
        if (!insideForeignPersonalSpace && await AnyGroupIsTenantAdminAsync(effectiveGroupIds, cancellationToken))
        {
            return TenantAdminRights;
        }

        // Data-classification clearance (ADR "Sensitivity clearance enforcement"): when the tenant enforces it,
        // a non-admin can't see a document whose sensitivity-label Rank exceeds their effective clearance (own ⊔
        // groups). "No CanSee" — returning NoRights hides it from every read/content/mutation path that
        // authorizes through this calculator. Off by default (labels stay informational).
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

            if (await IsBlockedByClearanceAsync(documentId, clearance, cancellationToken))
            {
                return NoRights;
            }
        }

        var governingDocumentId = await ResolveGoverningAclScopeAsync(documentId, cancellationToken);

        var matchingEntries = await _dbContext.AclEntries
            .Where(a => a.DocumentId == governingDocumentId
                && (a.UserId == userId || (a.GroupId.HasValue && effectiveGroupIds.Contains(a.GroupId.Value))))
            .ToListAsync(cancellationToken);

        var rights = BuildEffectiveRights(matchingEntries);

        // CanAccessWithoutGrant (ADR 0670): see + read where no grant exists, and NOTHING else. Applied last, so
        // it is reached only by a caller who is active, whose tenant is active, and who has passed clearance —
        // the right widens what a caller may READ, never who they are.
        return rights.CanSee || !await HoldsAccessWithoutGrantAsync(user.CanAccessWithoutGrant, effectiveGroupIds, cancellationToken)
            ? rights
            : rights with { CanSee = true, CanReadContent = true };
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
    private async Task<bool> IsBlockedByClearanceAsync(Guid documentId, int effectiveClearance, CancellationToken cancellationToken)
    {
        var labelRank = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == documentId && d.SensitivityLabelId != null)
            .Join(_dbContext.SensitivityLabelDefinitions, d => d.SensitivityLabelId, l => l.Id, (d, l) => (int?)l.Rank)
            .FirstOrDefaultAsync(cancellationToken);

        return labelRank is int rank && rank > effectiveClearance;
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
    private async Task<Guid> ResolveGoverningAclScopeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var currentId = documentId;

        while (true)
        {
            var current = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.Id == currentId)
                .Select(d => new { d.ParentId, d.BreaksInheritance })
                .SingleAsync(cancellationToken);

            if (current.BreaksInheritance || current.ParentId is not { } parentId)
            {
                return currentId;
            }

            currentId = parentId;
        }
    }

    // See ADR "ServiceAccount effective rights computation", ADR "ServiceAccount Document-scope effective
    // rights". No IsTenantAdmin-equivalent bypass (no such flag exists on ServiceAccount) and no group
    // expansion (GroupMembership.UserId is a real FK to User, not polymorphic, so a ServiceAccount cannot
    // belong to a Group at all) — just Tenant/ServiceAccount active checks, then a direct
    // AclEntry.ServiceAccountId match against the governing document.
    public async Task<EffectiveRights> GetEffectiveRightsForServiceAccountAsync(Guid serviceAccountId, Guid documentId, CancellationToken cancellationToken = default)
    {
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
            return NoRights;
        }

        // No IsTenantAdmin-equivalent bypass exists for a ServiceAccount (ADR "ServiceAccount effective rights
        // computation"), so clearance always applies (when enforced). Its clearance is just its own rank — a
        // ServiceAccount can't belong to a group.
        if (tenant.EnforceClearance && await IsBlockedByClearanceAsync(documentId, serviceAccount.ClearanceRank, cancellationToken))
        {
            return NoRights;
        }

        var governingDocumentId = await ResolveGoverningAclScopeAsync(documentId, cancellationToken);

        var matchingEntries = await GetMatchingEntriesForServiceAccountAsync(serviceAccountId, governingDocumentId, cancellationToken);

        var rights = BuildEffectiveRights(matchingEntries);

        // CanAccessWithoutGrant (ADR 0670), same shape as the User path — no group union, because a
        // ServiceAccount can't belong to a group, so its own column is the whole answer.
        return rights.CanSee || !serviceAccount.CanAccessWithoutGrant
            ? rights
            : rights with { CanSee = true, CanReadContent = true };
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
