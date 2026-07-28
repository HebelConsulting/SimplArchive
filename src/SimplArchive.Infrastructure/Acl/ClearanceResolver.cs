using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Acl;

// See ADR "Sensitivity clearance enforcement". A user's effective clearance = the max of their own
// ClearanceRank and every group they effectively belong to (direct + descendants, via GroupMembershipExpansion
// — the same set the ACL layer uses); Bypasses = the user (or any effective group) is a tenant admin. A
// ServiceAccount can't belong to a group, so its clearance is just its own rank and it never bypasses.
// Registered in AddInfrastructure. Consulted only when the tenant's EnforceClearance is on.
public class ClearanceResolver : IClearanceResolver
{
    private readonly SimplArchiveDbContext _dbContext;

    public ClearanceResolver(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<EffectiveClearance> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var own = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsTenantAdmin, u.ClearanceRank })
            .SingleAsync(cancellationToken);

        var rank = own.ClearanceRank;
        var bypasses = own.IsTenantAdmin;

        var effectiveGroupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);
        if (effectiveGroupIds.Count > 0)
        {
            var groups = await _dbContext.Groups
                .Where(g => effectiveGroupIds.Contains(g.Id))
                .Select(g => new { g.IsTenantAdmin, g.ClearanceRank })
                .ToListAsync(cancellationToken);
            foreach (var g in groups)
            {
                rank = Math.Max(rank, g.ClearanceRank);
                bypasses = bypasses || g.IsTenantAdmin;
            }
        }

        return new EffectiveClearance(rank, bypasses);
    }

    public async Task<EffectiveClearance> GetForServiceAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default)
    {
        var rank = await _dbContext.ServiceAccounts
            .Where(s => s.Id == serviceAccountId)
            .Select(s => s.ClearanceRank)
            .SingleAsync(cancellationToken);

        return new EffectiveClearance(rank, Bypasses: false);
    }
}
