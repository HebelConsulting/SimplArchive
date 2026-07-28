using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for sensitivity clearance enforcement (ADR "Sensitivity clearance enforcement") over the real API +
// Postgres + OpenSearch: with the tenant switch on, a low-clearance caller can't see, GET, or search a document
// labelled above their clearance (it's hidden from listings + search + a direct GET is denied), while unlabelled
// documents stay visible; raising their clearance restores access. Off by default nothing is hidden.
[Collection(E2ECollection.Name)]
public class SensitivityClearanceTests
{
    private readonly E2EApiFactory _factory;

    public SensitivityClearanceTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Enforced_clearance_hides_over_clearance_documents_from_listing_get_and_search()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var labels = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().ToList();
        var confidentialId = labels.Single(l => l.GetProperty("name").GetString() == "Confidential").GetProperty("id").GetGuid();

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Clr {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var word = $"clrword{Guid.NewGuid():N}";
        var confId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "secret" })).GetProperty("id").GetGuid();
        await UploadVersionAsync(owner, confId, $"content {word}\n");
        var openId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "open" })).GetProperty("id").GetGuid();
        await UploadVersionAsync(owner, openId, $"content {word}\n");
        (await owner.PutAsJsonAsync($"/api/documents/{confId}/sensitivity", new { labelId = confidentialId })).EnsureSuccessStatusCode();

        // Before enforcement the owner (a non-admin service account, clearance 0) sees both.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{confId}")).StatusCode);
        Assert.Contains(confId, await ChildIdsAsync(owner, repoId));

        // Turn enforcement on; the owner stays at clearance 0.
        await _factory.SetTenantEnforceClearanceAsync(tenantId, true);

        // The Confidential doc (rank 3 > clearance 0) is now hidden from the listing and a direct GET is denied,
        // while the unlabelled doc stays visible.
        var childrenNow = await ChildIdsAsync(owner, repoId);
        Assert.DoesNotContain(confId, childrenNow);
        Assert.Contains(openId, childrenNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/documents/{confId}")).StatusCode);

        // Search drops the over-clearance hit but keeps the unlabelled one.
        await PollAsync(async () =>
        {
            var ids = await SearchIdsAsync(owner, $"q={word}");
            return ids.Contains(openId) && !ids.Contains(confId);
        }, "search hides the over-clearance doc");

        // Raise the owner's clearance to 3 → the Confidential doc is visible again everywhere.
        await _factory.SetServiceAccountClearanceAsync(tenantId, clientId, 3);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{confId}")).StatusCode);
        Assert.Contains(confId, await ChildIdsAsync(owner, repoId));
        await PollAsync(async () => (await SearchIdsAsync(owner, $"q={word}")).Contains(confId), "search shows it after clearance raised");
    }

    private static async Task<HashSet<Guid>> ChildIdsAsync(HttpClient api, Guid parentId) =>
        (await TestJson.Get(api, $"/api/documents/{parentId}/children")).GetProperty("children")
            .EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToHashSet();

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient api, string query) =>
        (await TestJson.Get(api, $"/api/search?{query}")).GetProperty("results")
            .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();

    private static async Task UploadVersionAsync(HttpClient api, Guid docId, string content)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 30; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
    }
}
