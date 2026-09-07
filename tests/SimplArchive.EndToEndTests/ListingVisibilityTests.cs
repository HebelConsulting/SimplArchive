using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A row the caller cannot see is not listed (ADR 0765): the children listing and the per-folder references
// listing drop rows without CanSee instead of showing an unopenable NAME — a name is data too, and WebDAV
// already filtered this way, so the workbench naming what the mount hid was an ADR 0509 parity violation.
// The scenario is the one that surfaced it: siblings under a shared root, one with inheritance broken.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ListingVisibilityTests
{
    private readonly E2EApiFactory _factory;

    public ListingVisibilityTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_child_without_CanSee_is_absent_from_the_listing_not_just_inert()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(owner, "/api/repositories", new { name = $"Vis {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var visibleId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "visible" }))
            .GetProperty("id").GetGuid();
        var hiddenId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "hidden" }))
            .GetProperty("id").GetGuid();

        // Break the hidden child's inheritance FIRST, then grant the viewer on the root: the break snapshots
        // the then-current effective entries onto the child, so a root grant made before it would be baked in.
        await PutJson(owner, $"/api/documents/{hiddenId}/acl-entries/inheritance", new { breaksInheritance = true });

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
        var viewerId = await _factory.SeedUserAsync(tenantId, viewerEmail, "ViewerPw123!", "Viewer");
        await PutJson(owner, $"/api/documents/{repoId}/acl-entries/users/{viewerId}",
            new { canSee = true, canReadContent = true });
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "ViewerPw123!"));

        var names = (await GetJson(viewer, $"/api/documents/{repoId}/children")).GetProperty("children")
            .EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("visible", names);
        Assert.DoesNotContain("hidden", names);

        // The owner (creator, full rights) still sees both — the filter is per caller, not per folder.
        var ownerNames = (await GetJson(owner, $"/api/documents/{repoId}/children")).GetProperty("children")
            .EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("hidden", ownerNames);
    }

    [Fact]
    public async Task A_reference_whose_target_the_caller_cannot_see_is_absent_from_the_references_listing()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await PostJson(owner, "/api/repositories", new { name = $"RefVis {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var secretDocId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "secret-target" }))
            .GetProperty("id").GetGuid();
        var openDocId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "open-target" }))
            .GetProperty("id").GetGuid();
        var shelfId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "shelf" }))
            .GetProperty("id").GetGuid();
        await PostJson(owner, $"/api/documents/{shelfId}/references", new { targetId = secretDocId });
        await PostJson(owner, $"/api/documents/{shelfId}/references", new { targetId = openDocId });

        await PutJson(owner, $"/api/documents/{secretDocId}/acl-entries/inheritance", new { breaksInheritance = true });

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
        var viewerId = await _factory.SeedUserAsync(tenantId, viewerEmail, "ViewerPw123!", "Viewer");
        await PutJson(owner, $"/api/documents/{repoId}/acl-entries/users/{viewerId}",
            new { canSee = true, canReadContent = true });
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "ViewerPw123!"));

        var rows = (await GetJson(viewer, $"/api/documents/{shelfId}/references")).GetProperty("references")
            .EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains("open-target", rows);
        Assert.DoesNotContain("secret-target", rows);
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task PutJson(HttpClient client, string url, object body) =>
        (await client.PutAsJsonAsync(url, body)).EnsureSuccessStatusCode();

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
