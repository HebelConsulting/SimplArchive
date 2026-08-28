using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising saved searches (ADR "Saved searches"): a User saves a
// search, lists + deletes it, a duplicate name is rejected, they're private to the user, and a ServiceAccount
// has none.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SavedSearchesTests
{
    private readonly E2EApiFactory _factory;

    public SavedSearchesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Save_list_delete_conflict_privacy_and_service_account()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var email = $"searcher-{Guid.NewGuid():N}@e2e.local";
        const string password = "search1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Searcher");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Save a search.
        var created = await TestJson.Post(user, "/api/saved-searches", new { name = "My invoices", queryString = "q=invoice&system[documentType][eq]=Invoice" });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("My invoices", created.GetProperty("name").GetString());

        // It lists back with the query string.
        var list = (await TestJson.Get(user, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray().ToList();
        var mine = list.Single(s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal("q=invoice&system[documentType][eq]=Invoice", mine.GetProperty("queryString").GetString());

        // A duplicate name is rejected; an empty name/query is a 400.
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync("/api/saved-searches", new { name = "My invoices", queryString = "q=x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/saved-searches", new { name = "", queryString = "q=x" })).StatusCode);

        // Private: a second user doesn't see it.
        var otherEmail = $"other-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, otherEmail, password, "Other");
        using var other = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(otherEmail, password));
        Assert.Empty((await TestJson.Get(other, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray());

        // A ServiceAccount has no saved searches.
        using var service = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/api/saved-searches")).StatusCode);

        // Delete → gone.
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/saved-searches/{id}")).StatusCode);
        Assert.Empty((await TestJson.Get(user, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray());
    }

    [Fact]
    public async Task Scoped_sharing_everyone_specific_user_and_group_and_owner_only_edit()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        const string password = "share1234";

        var aEmail = $"share-a-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, aEmail, password, "Alice");
        using var alice = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aEmail, password));
        var bEmail = $"share-b-{Guid.NewGuid():N}@e2e.local";
        var bobId = await _factory.SeedUserAsync(tenantId, bEmail, password, "Bob");
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bEmail, password));
        var cEmail = $"share-c-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, cEmail, password, "Carol");
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(cEmail, password));

        // Alice shares one with EVERYONE and keeps one private.
        var shared = await TestJson.Post(alice, "/api/saved-searches", new { name = "Team invoices", queryString = "q=invoice", shareScope = 1 });
        var sharedId = shared.GetProperty("id").GetGuid();
        Assert.Equal(1, shared.GetProperty("shareScope").GetInt32());
        await TestJson.Post(alice, "/api/saved-searches", new { name = "My drafts", queryString = "q=draft", shareScope = 0 });

        // Bob sees the EVERYONE search (owner Alice, not mine) but NOT her private one.
        var bobList = (await TestJson.Get(bob, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray().ToList();
        var bobView = bobList.Single(s => s.GetProperty("id").GetGuid() == sharedId);
        Assert.False(bobView.GetProperty("isMine").GetBoolean());
        Assert.Equal("Alice", bobView.GetProperty("ownerName").GetString());
        Assert.DoesNotContain(bobList, s => s.GetProperty("name").GetString() == "My drafts");

        // Bob can't edit/delete it (owner-only → 404); nor read its shares.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/saved-searches/{sharedId}", new { name = "Hijacked", queryString = "q=x", shareScope = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/saved-searches/{sharedId}/shares")).StatusCode);

        // Alice narrows it to SPECIFIC: just Bob. Bob still sees it; Carol does not.
        (await alice.PutAsJsonAsync($"/api/saved-searches/{sharedId}", new { name = "Team invoices", queryString = "q=invoice", shareScope = 2, shares = new[] { new { type = "user", id = bobId } } })).EnsureSuccessStatusCode();
        Assert.Contains((await TestJson.Get(bob, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray(), s => s.GetProperty("id").GetGuid() == sharedId);
        Assert.DoesNotContain((await TestJson.Get(carol, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray(), s => s.GetProperty("id").GetGuid() == sharedId);

        // The owner reads back the grant.
        var shares = (await TestJson.Get(alice, $"/api/saved-searches/{sharedId}/shares")).GetProperty("shares").EnumerateArray().ToList();
        Assert.Equal(bobId, Assert.Single(shares).GetProperty("principalId").GetGuid());

        // Share with a GROUP Carol is in (membership flows down) → Carol now sees it.
        var groupId = await _factory.SeedGroupWithMemberAsync(tenantId, $"team-{Guid.NewGuid():N}", (await TestJson.Get(carol, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid());
        (await alice.PutAsJsonAsync($"/api/saved-searches/{sharedId}", new { name = "Team invoices", queryString = "q=invoice", shareScope = 2, shares = new[] { new { type = "group", id = groupId } } })).EnsureSuccessStatusCode();
        Assert.Contains((await TestJson.Get(carol, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray(), s => s.GetProperty("id").GetGuid() == sharedId);

        // Back to PRIVATE → neither Bob nor Carol sees it; Alice still does.
        (await alice.PutAsJsonAsync($"/api/saved-searches/{sharedId}", new { name = "Team invoices", queryString = "q=invoice", shareScope = 0 })).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await TestJson.Get(carol, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray(), s => s.GetProperty("id").GetGuid() == sharedId);
        Assert.Contains((await TestJson.Get(alice, "/api/saved-searches")).GetProperty("savedSearches").EnumerateArray(), s => s.GetProperty("id").GetGuid() == sharedId);

        // The share-targets picker lists active users + groups for any authenticated user.
        var targets = await TestJson.Get(bob, "/api/saved-searches/share-targets");
        Assert.Contains(targets.GetProperty("users").EnumerateArray(), u => u.GetProperty("id").GetGuid() == bobId);
    }
}
