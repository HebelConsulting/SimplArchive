using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Masks;

/// <summary>
/// The tenant's containment rules, loaded once per request and shared by everything that asks (#673).
/// </summary>
/// <remarks>
/// <para>
/// Two things ask what a folder admits: the <c>SaveChanges</c> invariant, which REFUSES a placement, and the
/// Api, which decides what to OFFER. They must never disagree — an offer the invariant refuses is an action
/// that fails on click, and a withheld offer hides one that would have worked. Sharing one loaded object
/// removes the possibility rather than testing for it.
/// </para>
/// <para>
/// Scoped, so the lifetime is one request or one unit of work. A mask edit is therefore picked up by the next
/// request with no invalidation to get wrong — see ADR 0655 for why a process-wide cache was rejected.
/// </para>
/// </remarks>
public interface IMaskContainmentProvider
{
    /// <param name="dbContext">
    /// The context to load through. Passed rather than injected because the DbContext itself is a caller, and
    /// injecting it here would make the two construct each other. The scope supplies the same instance to both,
    /// so this stays one context and one cache regardless of who asks first.
    /// </param>
    Task<MaskContainmentRules> ForAsync(SimplArchiveDbContext dbContext, Guid tenantId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class MaskContainmentProvider : IMaskContainmentProvider
{
    // Keyed by tenant: one request can legitimately touch more than one, and a single slot would hand the
    // wrong tenant's rules to the second — permissive or restrictive at random, and silent either way.
    private readonly Dictionary<Guid, MaskContainmentRules> _byTenant = [];

    public async Task<MaskContainmentRules> ForAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        if (!_byTenant.TryGetValue(tenantId, out var rules))
        {
            rules = await MaskContainmentRules.LoadAsync(dbContext, tenantId, cancellationToken);
            _byTenant[tenantId] = rules;
        }

        return rules;
    }
}
