using Microsoft.Extensions.Configuration;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Encryption;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.UnitTests;

// The SHIPPED router (ADR 0825) — routing, and the ACL contract that would leak if it answered wrongly.
//
// ISearchService.PreFiltersByAcl tells SearchController whether the hits it received are already
// ACL-filtered. A wrong FALSE costs a redundant CanSee check per hit; a wrong TRUE skips the check and shows
// a caller documents they cannot see. Those are not symmetrical, and a decorator answering for two
// implementations with different values is exactly where the wrong one gets returned.
//
// It exercises StrictTenantSearchRouter itself rather than a copy of its decision. An earlier version of this
// file re-implemented the routing in the test and asserted against that, which would have passed whatever the
// shipped class did — the seam on StrictTenantSearchPolicy.ExcludesAsync exists to make this possible.
public class StrictTenantSearchRouterTests
{
    [Fact]
    public void Before_any_search_the_router_claims_no_pre_filtering()
    {
        // The safe direction: the controller post-filters. Asserted because the property is readable before
        // SearchAsync has chosen anything, and "whatever the last caller left" would be worse than a default.
        Assert.False(Router(strict: false).Router.PreFiltersByAcl);
    }

    [Fact]
    public async Task An_ordinary_tenant_reaches_the_index_and_the_router_reports_its_pre_filtering()
    {
        var (router, indexed, metadata) = Router(strict: false);

        await router.SearchAsync("q", null, Access, Filters, 0, 10, CancellationToken.None);

        Assert.Equal(1, indexed.Calls);
        Assert.Equal(0, metadata.Calls);
        Assert.True(router.PreFiltersByAcl);
    }

    [Fact]
    public async Task A_strict_tenant_never_reaches_the_index_and_the_controller_is_told_to_post_filter()
    {
        var (router, indexed, metadata) = Router(strict: true);

        await router.SearchAsync("q", null, Access, Filters, 0, 10, CancellationToken.None);

        Assert.Equal(0, indexed.Calls);
        Assert.Equal(1, metadata.Calls);
        Assert.False(router.PreFiltersByAcl);
    }

    [Fact]
    public async Task No_ambient_tenant_takes_the_metadata_path()
    {
        // A caller with no tenant cannot be shown content text it might not be entitled to. Note the earlier
        // version of this test could not fail: its helper replaced a null tenant with a fresh Guid, so the
        // case it named was never the case it ran.
        var (router, indexed, metadata) = Router(strict: false, tenantId: null, tenantSupplied: false);

        await router.SearchAsync("q", null, Access, Filters, 0, 10, CancellationToken.None);

        Assert.Equal(0, indexed.Calls);
        Assert.Equal(1, metadata.Calls);
        Assert.False(router.PreFiltersByAcl);
    }

    private static readonly SearchAccess Access = new(BypassAcl: false, PrincipalTokens: []);
    private static readonly SearchFilters Filters = new([], [], null, null);

    private static (StrictTenantSearchRouter Router, Stub Indexed, Stub Metadata) Router(
        bool strict, Guid? tenantId = default, bool tenantSupplied = true)
    {
        var indexed = new Stub(preFilters: true);
        var metadata = new Stub(preFilters: false);
        var accessor = new StubTenant(tenantSupplied && tenantId is null ? Guid.NewGuid() : tenantId);

        return (new StrictTenantSearchRouter(indexed, metadata, accessor, new StubPolicy(strict)),
            indexed, metadata);
    }

    private sealed class StubPolicy(bool strict)
        : StrictTenantSearchPolicy(null!, new EncryptionModes(new ConfigurationBuilder().Build()))
    {
        public override Task<bool> ExcludesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(strict);
    }

    private sealed class StubTenant(Guid? tenantId) : ICurrentTenantAccessor
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    private sealed class Stub(bool preFilters) : ISearchService
    {
        public int Calls { get; private set; }

        public bool PreFiltersByAcl { get; } = preFilters;

        public Task<SearchPage> SearchAsync(
            string query, Guid? repositoryId, SearchAccess access, SearchFilters filters,
            int skip, int take, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SearchPage([], false));
        }
    }
}
