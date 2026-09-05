using System.Text.Json;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The personal space is a real folder carrying the UserFolder mask (PersonalRepositoryProvisioner), so its
// resource must advertise the `mask` and `index-data` rels exactly as a repository row does. It did not, so a
// client that follows `mask` to fill the detail pane found nothing and threw the ADR 0543 "rel not advertised
// for 'Demo Admin'" error the desktop hit on the kiosk. Following the rel must yield the UserFolder mask.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class PersonalRepositoryMaskRelTests
{
    private readonly E2EApiFactory _factory;

    public PersonalRepositoryMaskRelTests(E2EApiFactory factory) => _factory = factory;

    private static string Href(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    [Fact]
    public async Task Personal_repository_advertises_its_mask_and_it_is_the_user_folder_mask()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"pr-mask-{Guid.NewGuid():N}@e2e.local";
        const string password = "pr-mask-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Demo Admin");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });

        // The rels must be present — their absence is exactly what the desktop hit.
        var maskHref = Href(personal, "mask");
        var indexHref = Href(personal, "index-data");

        // Following the mask rel yields the UserFolder mask, not a throw or an empty node.
        var mask = await TestJson.Get(api, maskHref);
        Assert.Equal(WellKnownMaskIds.UserFolder, mask.GetProperty("maskId").GetGuid());
        Assert.Equal("User Folder", mask.GetProperty("name").GetString());

        // And index-data resolves (an empty field set is fine — the point is the rel is followable).
        _ = await TestJson.Get(api, indexHref);
    }
}
