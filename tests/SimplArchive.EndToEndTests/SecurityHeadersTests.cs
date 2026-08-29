namespace SimplArchive.EndToEndTests;

// The security headers are on the wire (ADR 0084, issue #844).
//
// Asserted against a real response rather than by reading the middleware, because that is the only way to catch
// the two ways this silently stops working: the middleware being registered too late in the pipeline to cover a
// response, and a header being set but overwritten downstream. The app shipped with NONE of these for a long
// time and nothing was visibly wrong — which is exactly why the guard has to look at what a browser receives.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SecurityHeadersTests
{
    private readonly E2EApiFactory _factory;

    public SecurityHeadersTests(E2EApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health/ready")]  // anonymous, and served before authentication
    [InlineData("/api")]           // the API root
    public async Task Every_response_carries_the_hardening_headers(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.True(response.Headers.Contains("Content-Security-Policy"), $"no CSP on {path}");
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.True(response.Headers.Contains("Permissions-Policy"), $"no Permissions-Policy on {path}");
    }

    [Fact]
    public async Task The_policy_refuses_inline_script_and_allows_the_wasm_runtime()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        var scriptSrc = policy.Split("; ").Single(d => d.StartsWith("script-src", StringComparison.Ordinal));

        Assert.DoesNotContain("unsafe-inline", scriptSrc);
        Assert.Contains("'wasm-unsafe-eval'", scriptSrc);
    }

    [Fact]
    public async Task HSTS_is_absent_unless_a_deployment_asked_for_it()
    {
        // A promise the browser remembers for a year, made on a deployment that may not be on HTTPS at all,
        // has no server-side undo. Off unless configured — and the test host is not configured.
        var response = await _factory.CreateClient().GetAsync("/health/ready");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}
