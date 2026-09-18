using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The XML representation deals in instants too (#1259, ADRs 0548 + 0802).
//
// WHAT WAS WRONG. `UtcDateTimeOffsetConverter` normalises every inbound `DateTimeOffset` to UTC because
// Postgres stores an instant and Npgsql refuses to write any other offset. It is a System.Text.Json converter,
// and the XML formatter (ADR 0190) runs no STJ converters — so an XML caller posting `+02:00` reached
// SaveChanges with the offset intact and died as a bare 500, far from the code that accepted it. The rule held
// on one negotiated representation and silently did not hold on the other.
//
// WHY THE OFFSET IS NON-ZERO HERE, and why that is the whole test. Every other fixture in this repository
// writes `UtcNow` and every runner is UTC, so a test written that way asserts nothing about offset handling —
// which is the documented reason the original bug survived a full green suite. `+02:00` is the fixture
// precisely because it is the thing that breaks.
//
// The endpoint is external-link creation on purpose: it is where ADR 0548's bug was originally found, from the
// desktop client in CEST, while every test and the web client (sending TimeSpan.Zero) stayed green.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class XmlTimeContractTests
{
    private readonly E2EApiFactory _factory;

    public XmlTimeContractTests(E2EApiFactory factory) => _factory = factory;

    private const string Xml = "application/vnd.simplarchive.v1+xml";

    [Fact]
    public async Task An_XML_caller_may_post_a_timestamp_in_its_own_zone()
    {
        var (api, docId) = await SeedShareableDocumentAsync();

        // Three days out in CEST — comfortably inside the tenant's maximum, so a rejection here can only be
        // about the offset and not about the expiry policy.
        // Kind must be Unspecified: a Utc-kind DateTime refuses a non-zero offset outright ("The UTC Offset for
        // Utc DateTime instances must be 0"), which is the framework saying the same thing this test is about.
        var wallClock = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(3).Date.AddHours(9), DateTimeKind.Unspecified);
        var expires = new DateTimeOffset(wallClock, TimeSpan.FromHours(2));
        var body = "<CreateExternalLinkRequest>"
            + $"<ExpiresAt>{expires:yyyy-MM-ddTHH:mm:sszzz}</ExpiresAt>"
            + "</CreateExternalLinkRequest>";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/documents/{docId}/external-links")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(Xml) { CharSet = "utf-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Xml));

        var response = await api.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        // Before the fix this was a 500 from deep inside SaveChanges — so the status code alone is the
        // regression assertion, and the message names what to look for if it ever comes back.
        Assert.True(response.IsSuccessStatusCode,
            $"An XML caller posting a non-UTC offset must be accepted, not 500 at SaveChanges. Got "
            + $"{(int)response.StatusCode}: {payload}");

        // And the instant survived the normalisation — this is a conversion, not a truncation. The stored value
        // is written back as Zulu, which is the outbound half of ADR 0802 holding on this representation.
        var expectedUtc = expires.ToUniversalTime();
        Assert.Contains(expectedUtc.ToString("yyyy-MM-ddTHH:mm:ss"), payload, StringComparison.Ordinal);
        Assert.Contains("Z", payload, StringComparison.Ordinal);

        // The wall clock the caller sent must NOT be what came back: that is the difference between "we stored
        // the instant" and "we stored the digits", and only an offset that is not zero can tell them apart.
        Assert.DoesNotContain(expires.ToString("yyyy-MM-ddTHH:mm:ss") + "+02:00", payload, StringComparison.Ordinal);
    }

    // The tenant kill switch is off by default, and there is no endpoint that turns it on for a fresh tenant —
    // so it is set directly, the same way ExternalLinkApiTests does it.
    private async Task AllowExternalLinksAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(t => t.Id == tenantId);
        tenant.AllowExternalLinks = true;
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Api, Guid DocumentId)> SeedShareableDocumentAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await AllowExternalLinksAsync(tenantId);

        var email = $"xmltime-{Guid.NewGuid():N}@e2e.local";
        const string password = "xmltimepw1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Xml Time",
            canManageRepositories: true, canCreateExternalLink: true, isTenantAdmin: true);

        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"XmlTime {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "xml-timed-doc" }))
            .GetProperty("id").GetGuid();

        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("shared content")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return (api, docId);
    }
}
