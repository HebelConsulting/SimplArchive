using System.Net;
using System.Net.Http.Headers;

namespace SimplArchive.EndToEndTests;

// Media-type API version negotiation (ADR 0189) — previously untested, which is how the gap below survived.
//
// The rule the API states: the version lives in the media-type subtype, and a request that does NOT name a
// version implicitly gets the current one. The gap was the case in between — an Accept that names OUR media
// type in a shape the reader could not parse was treated as "no version requested", so a caller who explicitly
// asked for v2 was served v1 and never told (#595, ADR 0626).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ApiVersionNegotiationTests
{
    private readonly E2EApiFactory _factory;

    public ApiVersionNegotiationTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"ver-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ver-1234", "Version User");
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ver-1234"));
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", accept);
        return await client.SendAsync(request);
    }

    [Theory]
    // The documented default path: no version named at all — a plain JSON request, or a browser's */* — is
    // served the current version. This must stay silent and successful.
    [InlineData("application/json")]
    [InlineData("*/*")]
    // The explicit, well-formed forms, both negotiable formats (ADR 0190).
    [InlineData("application/vnd.simplarchive.v1+json")]
    [InlineData("application/vnd.simplarchive.v1+xml")]
    public async Task A_readable_request_is_served(string accept)
    {
        using var client = await ClientAsync();
        var response = await GetAsync(client, accept);

        Assert.True(response.IsSuccessStatusCode, $"Accept: {accept} → {(int)response.StatusCode}");
    }

    [Theory]
    // A version we do not serve, in the fully-specified form — already handled before this change.
    [InlineData("application/vnd.simplarchive.v99+json")]
    // THE REGRESSION. Our media type, an explicit version we do not serve, but no format suffix — the reader
    // required "+json"/"+xml" to see a version at all, so this read as "no version requested" and was quietly
    // served v1. The caller asked for v99 and got v1 with a 200.
    [InlineData("application/vnd.simplarchive.v99")]
    public async Task An_unsupported_version_is_refused_rather_than_quietly_downgraded(string accept)
    {
        using var client = await ClientAsync();
        var response = await GetAsync(client, accept);

        Assert.False(response.IsSuccessStatusCode,
            $"Accept: {accept} was served successfully — the caller asked for a version we do not have and was not told");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_unsupported_version_is_told_why()
    {
        using var client = await ClientAsync();

        // The refusal above is unambiguous but bodiless for the suffix-less form: no formatter can produce
        // "application/vnd.simplarchive.v99", so the problem document cannot be serialised into what the caller
        // asked for. When the caller names a format we CAN write, they get the reason as well as the status.
        var response = await GetAsync(client, "application/vnd.simplarchive.v99+json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNSUPPORTED_API_VERSION", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Our_media_type_without_any_version_is_served_and_flagged()
    {
        using var client = await ClientAsync();

        // No version to extract, but the caller clearly meant us — served on the default version rather than
        // refused, because rejecting an Accept we merely failed to parse would be a worse answer than serving
        // the only version we have. The Warning (SimplArchive.Api.Versioning) is what makes it visible; the
        // observable contract here is that the request still succeeds.
        var response = await GetAsync(client, "application/vnd.simplarchive+json");

        Assert.True(response.IsSuccessStatusCode, $"status={(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_negotiated_version_is_reported_back()
    {
        using var client = await ClientAsync();

        // ReportApiVersions is on, so a caller can discover what it actually got — which is the other half of
        // not being downgraded silently: the answer says which version answered.
        var response = await GetAsync(client, "application/vnd.simplarchive.v1+json");

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(
            response.Headers.Contains("api-supported-versions"),
            "the response does not report which versions are supported, so a client cannot tell what it was served");
    }
}
