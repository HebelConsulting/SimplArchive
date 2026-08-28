using System.Net;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API, exercising the OpenAPI definition endpoint (ADR "OpenAPI definition endpoint"):
// the machine-readable /openapi/v1.json document is served anonymously and enumerates the real routes; the
// Scalar interactive UI is mapped in Development (the E2E factory runs as Development); and the /api root
// discovery document advertises the spec via an "openApi" link.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class OpenApiEndpointTests
{
    private readonly E2EApiFactory _factory;

    public OpenApiEndpointTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_document_is_served_anonymously_and_describes_the_api()
    {
        using var anon = _factory.CreateClient();

        var response = await anon.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Not rewritten by VersionedContentTypeMiddleware (which only touches /api) — importers require plain JSON.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        // A valid OpenAPI 3.x document with an info block and enumerated paths.
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());
        Assert.True(root.TryGetProperty("info", out _));

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/repositories", out _), "the spec should enumerate the real controller routes");
    }

    [Fact]
    public async Task Scalar_ui_is_served_in_development()
    {
        using var anon = _factory.CreateClient();

        var response = await anon.GetAsync("/scalar/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SimplArchive API", body);
    }

    [Fact]
    public async Task Root_discovery_document_advertises_the_openapi_link()
    {
        using var anon = _factory.CreateClient();

        var root = await TestJson.Get(anon, "/api");
        var links = root.GetProperty("links").EnumerateArray().ToList();
        var openApi = links.SingleOrDefault(l => l.GetProperty("rel").GetString() == "openApi");
        Assert.Equal("/openapi/v1.json", openApi.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Root_discovery_document_carries_the_server_version()
    {
        using var anon = _factory.CreateClient();

        // The desktop self-update check (issue #312, ADR 0512) reads serverVersion to decide whether the running
        // client is behind THIS deployment before looking for a matching client release on GitHub.
        var root = await TestJson.Get(anon, "/api");
        Assert.True(root.TryGetProperty("serverVersion", out var version), "the /api root should expose serverVersion");
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));
    }
}
