using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Acl;

// See ADR "Enforce group system rights for members". A user's effective system rights = their own rights
// unioned with the rights of every group in their effective group set (direct + descendants, via
// GroupMembershipExpansion — the same "flows down" model the ACL layer uses). Registered in
// AddInfrastructure. No IsActive/Tenant.Status gate here (unlike EffectiveRightsCalculator's ACL path) —
// this preserves the existing management-endpoint behavior, which never gated on those; a deactivated user
// can't obtain a token to reach these endpoints in the first place.
public class UserSystemRightsResolver : IUserSystemRightsResolver
{
    private readonly SimplArchiveDbContext _dbContext;

    public UserSystemRightsResolver(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SystemRightsSet> GetEffectiveSystemRightsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var own = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new SystemRightsSet(
                u.IsTenantAdmin, u.CanImpersonate, u.CanOverrideCheckout, u.CanLegalHold,
                u.CanManageClassification, u.CanResetMfa, u.CanManageRepositories, u.CanManageMasks,
                u.CanManageServiceAccounts, u.CanManageUsers, u.CanViewAuditLog, u.CanExport, u.CanImport,
                u.CanManageInboxes, u.CanCreateExternalLink))
            .SingleAsync(cancellationToken);

        var effectiveGroupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);

        if (effectiveGroupIds.Count == 0)
        {
            return own;
        }

        var groupRights = await _dbContext.Groups
            .Where(g => effectiveGroupIds.Contains(g.Id))
            .Select(g => new SystemRightsSet(
                g.IsTenantAdmin, g.CanImpersonate, g.CanOverrideCheckout, g.CanLegalHold,
                g.CanManageClassification, g.CanResetMfa, g.CanManageRepositories, g.CanManageMasks,
                g.CanManageServiceAccounts, g.CanManageUsers, g.CanViewAuditLog, g.CanExport, g.CanImport,
                g.CanManageInboxes, g.CanCreateExternalLink))
            .ToListAsync(cancellationToken);

        return groupRights.Aggregate(own, (acc, rights) => acc.Union(rights));
    }
}
