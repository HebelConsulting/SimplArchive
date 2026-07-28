namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves a principal's <em>effective</em> data-classification clearance (ADR "Sensitivity clearance
/// enforcement"). For a <c>User</c> it is the maximum of their own <c>ClearanceRank</c> and every group they
/// effectively belong to (direct + descendants, the same "membership flows down" expansion the ACL layer
/// uses); for a <c>ServiceAccount</c> it is just its own rank (no groups). <see cref="EffectiveClearance.Bypasses"/>
/// is true when the principal is (or belongs to a group that is) a tenant admin — an admin bypasses clearance
/// exactly as they bypass the ACL (ADR "Tenant admin ACL bypass"). Only consulted when the tenant's
/// <c>EnforceClearance</c> is on.
/// </summary>
public interface IClearanceResolver
{
    Task<EffectiveClearance> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<EffectiveClearance> GetForServiceAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default);
}

/// <summary>The clearance a principal effectively holds, plus whether they bypass clearance entirely (tenant admin).</summary>
public sealed record EffectiveClearance(int Rank, bool Bypasses);
