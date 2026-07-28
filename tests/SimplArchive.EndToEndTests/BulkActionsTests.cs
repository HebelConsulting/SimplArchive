using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising bulk actions on a set of selected documents (ADR "Bulk
// actions on selected documents"): add-tags, set-sensitivity, move, and delete each act on many items in one
// call, authorizing per item (an item the caller can't edit is skipped and reported).
[Collection(E2ECollection.Name)]
public class BulkActionsTests
{
    private readonly E2EApiFactory _factory;

    public BulkActionsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Bulk_add_tags_set_sensitivity_move_and_delete()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Bulk-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var targetId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Target" })).GetProperty("id").GetGuid();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"doc-{i}" })).GetProperty("id").GetGuid());
        }

        // Add tags to all three (union).
        var tagged = await TestJson.Post(owner, "/api/documents/bulk/tags", new { ids, tags = new[] { "Batch", "reviewed" } });
        Assert.Equal(3, tagged.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, tagged.GetProperty("skipped").GetInt32());
        foreach (var id in ids)
        {
            var tags = (await TestJson.Get(owner, $"/api/documents/{id}/tags")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
            Assert.Equal(new[] { "batch", "reviewed" }, tags);
        }

        // Set sensitivity = Confidential on all three (by label id).
        var confidentialId = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Confidential").GetProperty("id").GetGuid();
        var classified = await TestJson.Post(owner, "/api/documents/bulk/sensitivity", new { ids, labelId = confidentialId });
        Assert.Equal(3, classified.GetProperty("succeeded").GetInt32());
        foreach (var id in ids)
        {
            Assert.Equal(confidentialId, (await TestJson.Get(owner, $"/api/documents/{id}")).GetProperty("sensitivityLabelId").GetGuid());
        }

        // Move the first two into the Target folder.
        var moved = await TestJson.Post(owner, "/api/documents/bulk/move", new { ids = ids.Take(2), parentId = targetId });
        Assert.Equal(2, moved.GetProperty("succeeded").GetInt32());
        var targetChildren = (await TestJson.Get(owner, $"/api/documents/{targetId}/children")).GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(ids[0], targetChildren);
        Assert.Contains(ids[1], targetChildren);

        // Delete all three (soft-delete to recycle bin).
        var deleted = await TestJson.Post(owner, "/api/documents/bulk/delete", new { ids });
        Assert.Equal(3, deleted.GetProperty("succeeded").GetInt32());
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/documents/{id}")).StatusCode);
        }
    }

    [Fact]
    public async Task Skips_items_the_caller_may_not_edit()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A user in the same tenant with no rights on the owner's documents.
        var email = $"bulk-out-{Guid.NewGuid():N}@e2e.local";
        const string password = "bulk-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Outsider");
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Bulk-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "doc" })).GetProperty("id").GetGuid();

        // The outsider can see neither, so every item is skipped (none succeed).
        var result = await TestJson.Post(outsider, "/api/documents/bulk/tags", new { ids = new[] { docId }, tags = new[] { "x" } });
        Assert.Equal(0, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped").GetInt32());
    }
}
