using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// Saving a search with specific shares used to be TWO transactions (#1171): the SavedSearch row committed, then
// ApplySharesAsync committed the SavedSearchShare rows separately. One Save click, two commits — so a failure on
// the second left a saved search with NONE of the shares the user had picked, and no error they could act on.
//
// Both Create and Update now stage the search and its shares together and commit once.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SavedSearchShareAtomicityTests
{
    private readonly E2EApiFactory _factory;

    public SavedSearchShareAtomicityTests(E2EApiFactory factory) => _factory = factory;

    private const int ShareWithSpecific = 2;

    private async Task<(HttpClient Owner, Guid ColleagueId)> WorldAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var owner = $"share-owner-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, owner, "share-1234", "Share Owner");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(owner, "share-1234"));

        var colleague = $"share-mate-{Guid.NewGuid():N}@e2e.local";
        var colleagueId = await _factory.SeedUserAsync(tenantId, colleague, "share-1234", "Share Mate");

        return (api, colleagueId);
    }

    private static async Task<int> ShareCountAsync(HttpClient api, Guid searchId) =>
        (await TestJson.Get(api, $"/api/saved-searches/{searchId}/shares")).GetProperty("shares").EnumerateArray().Count();

    [Fact]
    public async Task A_created_search_has_its_shares_the_moment_it_exists()
    {
        var (api, colleagueId) = await WorldAsync();
        using var _a = api;

        var created = await TestJson.Post(api, "/api/saved-searches", new
        {
            name = $"atomic-{Guid.NewGuid():N}"[..14],
            queryString = "q=invoice",
            shareScope = ShareWithSpecific,
            shares = new[] { new { type = "user", id = colleagueId } },
        });

        // The search and its share are one act. Read back immediately: under two transactions the second could
        // fail and leave exactly this response describing a search with no shares behind it.
        Assert.Equal(1, await ShareCountAsync(api, created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task An_edited_search_keeps_its_name_and_its_shares_in_step()
    {
        var (api, colleagueId) = await WorldAsync();
        using var _a = api;

        var created = await TestJson.Post(api, "/api/saved-searches", new
        {
            name = $"atomic-{Guid.NewGuid():N}"[..14],
            queryString = "q=invoice",
            shareScope = 0,
        });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(0, await ShareCountAsync(api, id));

        var renamed = $"renamed-{Guid.NewGuid():N}"[..14];
        var response = await api.PutAsJsonAsync($"/api/saved-searches/{id}", new
        {
            name = renamed,
            queryString = "q=contract",
            shareScope = ShareWithSpecific,
            shares = new[] { new { type = "user", id = colleagueId } },
        });
        response.EnsureSuccessStatusCode();

        // One Save click moved BOTH the name and the share set. Separately committed, the rename could land
        // while the shares did not.
        var listed = (await TestJson.Get(api, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray()
            .First(s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal(renamed, listed.GetProperty("name").GetString());
        Assert.Equal(1, await ShareCountAsync(api, id));
    }
}
