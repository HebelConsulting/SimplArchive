using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Search;

/// <summary>
/// Routes a strict tenant's searches to the metadata service, and everyone else's to the index.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a different registration, because the search service is chosen once for the whole
/// installation — from <c>OpenSearch:Url</c> — while the tier is a decision per TENANT. One installation runs
/// both, the same shape as the kiosk running an encrypted demo tenant beside an untouched public one.
/// </para>
/// <para>
/// <b>The metadata service is not a reduced stand-in written for this tier.</b> It is the implementation that
/// already serves every installation with no OpenSearch configured (ADR 0249), so it is exercised by the
/// existing suite rather than being a second code path only strict tenants reach — which is how a fallback
/// comes to be subtly broken without anyone noticing.
/// </para>
/// <para>
/// <b>Routing alone would be the appearance of the property rather than the property.</b> Document body text
/// is absent from the index for these tenants because <see cref="StrictTenantSearchPolicy"/> keeps it out;
/// stopping only the query would leave that plaintext copy in place and merely stop reading it.
/// </para>
/// </remarks>
/// <remarks>
/// The two services arrive as <see cref="ISearchService"/> rather than their concrete types, and the
/// registration picks them out of the container instead. That is what lets the REAL router be tested with
/// stubs: with concrete parameters a test can only re-implement the decision and assert against its own copy,
/// which passes whatever the shipped class does.
/// </remarks>
public sealed class StrictTenantSearchRouter(
    ISearchService indexed,
    ISearchService metadataOnly,
    ICurrentTenantAccessor tenant,
    StrictTenantSearchPolicy policy) : ISearchService
{
    private ISearchService? _used;

    /// <summary>
    /// Whether the hits the last search returned were already ACL-filtered.
    /// </summary>
    /// <remarks>
    /// Answered from the service that actually ran, which is sound because <c>SearchController</c> reads this
    /// only while iterating the page it just received — the sole consumer, checked.
    ///
    /// The default before any search is <c>false</c>, and the asymmetry is deliberate: <c>false</c> makes the
    /// controller post-filter every hit with <c>CanSee</c>, so a wrong answer costs a redundant check. A wrong
    /// <c>true</c> would skip the check entirely and show a caller documents they cannot see. One direction is
    /// a little slower; the other is a leak.
    /// </remarks>
    public bool PreFiltersByAcl => _used?.PreFiltersByAcl ?? false;

    public async Task<SearchPage> SearchAsync(
        string query, Guid? repositoryId, SearchAccess access, SearchFilters filters,
        int skip, int take, CancellationToken cancellationToken)
    {
        // No ambient tenant also takes the metadata path: a caller without one cannot be shown content text
        // it might not be entitled to, and the metadata service leaves the ACL check to the controller.
        _used = tenant.TenantId is { } tenantId && !await policy.ExcludesAsync(tenantId, cancellationToken)
            ? indexed
            : metadataOnly;

        return await _used.SearchAsync(query, repositoryId, access, filters, skip, take, cancellationToken);
    }
}
