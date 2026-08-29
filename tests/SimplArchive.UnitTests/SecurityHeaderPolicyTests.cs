using Microsoft.Extensions.Configuration;
using SimplArchive.Api.Security;

namespace SimplArchive.UnitTests;

// The composed content-security policy (ADR 0084, issue #844).
//
// Covered here rather than only end-to-end because the failure mode is silent and remote: a policy that omits
// the object-storage origin does not fail a request — the browser refuses the upload, the server logs nothing,
// and the app looks healthy from every angle a server-side test can see.
public class SecurityHeaderPolicyTests
{
    [Fact]
    public void The_storage_origin_is_derived_from_configuration_and_named_where_the_browser_needs_it()
    {
        // The browser uploads and previews straight to storage over a presigned URL, so both fetches
        // (connect-src) and rendered images (img-src) must name that origin.
        var policy = SecurityHeaders.ComposePolicy("https://storage.example.com", null);

        Assert.Contains("connect-src 'self' https://storage.example.com", policy);
        Assert.Contains("img-src 'self' data: blob: https://storage.example.com", policy);
    }

    [Theory]
    [InlineData("https://s3.example.com:8333/bucket/thing", "https://s3.example.com:8333")]
    [InlineData("http://localhost:8333", "http://localhost:8333")]
    public void The_origin_is_taken_from_the_url_the_BROWSER_uses(string configured, string expected)
    {
        // PublicServiceUrl, not ServiceUrl: in a split-network deployment the API reaches storage by an
        // internal name the browser cannot resolve, and naming that one would be a policy about the wrong host.
        var configuration = Configuration(("ObjectStorage:PublicServiceUrl", configured), ("ObjectStorage:ServiceUrl", "http://internal:8333"));

        Assert.Equal(expected, SecurityHeaders.StorageOrigin(configuration));
    }

    [Fact]
    public void The_internal_url_is_the_fallback_when_no_public_one_is_configured()
    {
        var configuration = Configuration(("ObjectStorage:ServiceUrl", "http://seaweedfs:8333"));

        Assert.Equal("http://seaweedfs:8333", SecurityHeaders.StorageOrigin(configuration));
    }

    [Fact]
    public void An_unconfigured_store_yields_no_origin_rather_than_a_broken_policy_fragment()
    {
        Assert.Null(SecurityHeaders.StorageOrigin(Configuration()));

        var policy = SecurityHeaders.ComposePolicy(null, null);
        Assert.Contains("connect-src 'self';", policy);
        Assert.DoesNotContain("  ", policy); // no gap where an origin would have gone
    }

    [Fact]
    public void Scripts_may_not_be_inline_which_is_the_part_that_answers_cross_site_scripting()
    {
        var policy = SecurityHeaders.ComposePolicy(null, null);
        var scriptSrc = policy.Split("; ").Single(d => d.StartsWith("script-src", StringComparison.Ordinal));

        // The load-bearing assertion. Injected inline script is what a content-security policy is FOR, and the
        // server-rendered pages carry a per-request nonce rather than a blanket allowance.
        Assert.DoesNotContain("unsafe-inline", scriptSrc);
        Assert.Contains("nonce-", scriptSrc);

        // This test first asserted that 'unsafe-eval' was absent too, and that assertion was WRONG about the
        // application rather than about the policy: with every other directive permissive and only script-src
        // strict, registering a passkey threw inside the client framework's renderer, reproducibly — while
        // sign-in and every HTTP call looked healthy (#844). Recorded as an assertion rather than deleted, so
        // the concession stays deliberate: if a future framework version no longer needs it, this line is where
        // that is noticed.
        Assert.Contains("'wasm-unsafe-eval'", scriptSrc);
        Assert.Contains("'unsafe-eval'", scriptSrc);
    }

    [Fact]
    public void Framing_is_same_origin_because_the_silent_token_renewal_frames_us()
    {
        // 'none' would break OIDC silent renew, which presents as a random sign-out rather than as a CSP error.
        Assert.Contains("frame-ancestors 'self'", SecurityHeaders.ComposePolicy(null, null));
    }

    [Fact]
    public void Additional_sources_are_appended_for_a_topology_this_code_cannot_know()
    {
        var policy = SecurityHeaders.ComposePolicy("https://storage.example.com", "https://cdn.example.com");

        Assert.Contains("connect-src 'self' https://storage.example.com https://cdn.example.com", policy);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
