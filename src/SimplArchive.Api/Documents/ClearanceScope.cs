using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Resolves the current caller's data-classification clearance scope for <em>bulk</em> filtering — folder
/// listings and search, which don't authorize each row through <c>IEffectiveRightsCalculator</c> the way a
/// single-document GET does (ADR "Sensitivity clearance enforcement"). The per-document <c>CanSee</c> authority
/// stays the calculator; this is its counterpart for queries. Unrestricted when the tenant doesn't enforce
/// clearance, the caller is a platform/tenant admin, or no label out-ranks the caller.
/// </summary>
public interface IClearanceScopeResolver
{
    Task<ClearanceScope> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A caller's clearance scope: either unrestricted, or a set of sensitivity-label ids the caller may NOT access
/// (label <c>Rank</c> &gt; effective clearance) plus the numeric ceiling (for the search-index rank filter).
/// </summary>
public sealed class ClearanceScope
{
    public static readonly ClearanceScope Unrestricted = new(true, [], int.MaxValue);

    private readonly HashSet<Guid> _forbiddenLabelIds;

    private ClearanceScope(bool isUnrestricted, HashSet<Guid> forbiddenLabelIds, int maxRank)
    {
        IsUnrestricted = isUnrestricted;
        _forbiddenLabelIds = forbiddenLabelIds;
        MaxRank = maxRank;
    }

    public bool IsUnrestricted { get; }

    /// <summary>The caller's effective clearance (the max label <c>Rank</c> they may see); <see cref="int.MaxValue"/> when unrestricted.</summary>
    public int MaxRank { get; }

    public IReadOnlyCollection<Guid> ForbiddenLabelIds => _forbiddenLabelIds;

    public static ClearanceScope Restricted(int maxRank, HashSet<Guid> forbiddenLabelIds) =>
        new(false, forbiddenLabelIds, maxRank);

    /// <summary>Whether the caller may access a document carrying the given label (null/unlabelled is always allowed).</summary>
    public bool Allows(Guid? labelId) =>
        IsUnrestricted || labelId is null || !_forbiddenLabelIds.Contains(labelId.Value);

    /// <summary>Narrows a document query to only rows the caller's clearance permits (unlabelled always kept).</summary>
    public IQueryable<Document> Filter(IQueryable<Document> query) =>
        IsUnrestricted
            ? query
            : query.Where(d => d.SensitivityLabelId == null || !_forbiddenLabelIds.Contains(d.SensitivityLabelId.Value));
}

public sealed class ClearanceScopeResolver : IClearanceScopeResolver
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _tenant;
    private readonly ICurrentUserAccessor _user;
    private readonly ICurrentServiceAccountAccessor _serviceAccount;
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministrator;
    private readonly IClearanceResolver _clearance;

    public ClearanceScopeResolver(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor tenant,
        ICurrentUserAccessor user,
        ICurrentServiceAccountAccessor serviceAccount,
        ICurrentPlatformAdministratorAccessor platformAdministrator,
        IClearanceResolver clearance)
    {
        _dbContext = dbContext;
        _tenant = tenant;
        _user = user;
        _serviceAccount = serviceAccount;
        _platformAdministrator = platformAdministrator;
        _clearance = clearance;
    }

    public async Task<ClearanceScope> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // A platform administrator has no tenant context and bypasses everything.
        if (_platformAdministrator.PlatformAdministratorId is not null || _tenant.TenantId is not { } tenantId)
        {
            return ClearanceScope.Unrestricted;
        }

        var enforce = await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.EnforceClearance)
            .FirstOrDefaultAsync(cancellationToken);
        if (!enforce)
        {
            return ClearanceScope.Unrestricted;
        }

        EffectiveClearance? clearance = _user.UserId is { } userId
            ? await _clearance.GetForUserAsync(userId, cancellationToken)
            : _serviceAccount.ServiceAccountId is { } serviceAccountId
                ? await _clearance.GetForServiceAccountAsync(serviceAccountId, cancellationToken)
                : null;

        // A tenant admin bypasses clearance (like the ACL bypass); an unresolvable caller is treated as
        // unrestricted (they'll be denied elsewhere by the ACL layer).
        if (clearance is null || clearance.Bypasses)
        {
            return ClearanceScope.Unrestricted;
        }

        var forbidden = await _dbContext.SensitivityLabelDefinitions
            .Where(l => l.Rank > clearance.Rank)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        return forbidden.Count == 0
            ? ClearanceScope.Unrestricted
            : ClearanceScope.Restricted(clearance.Rank, forbidden.ToHashSet());
    }
}
