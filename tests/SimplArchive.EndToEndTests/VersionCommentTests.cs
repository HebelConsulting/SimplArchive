using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API for ADR 0528: a version can carry a "why this revision" comment, set at creation
// and read back from the versions list.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class VersionCommentTests
{
    private readonly E2EApiFactory _factory;

    public VersionCommentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Version_comment_round_trips_through_create_and_list()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"VC {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "Report" })).GetProperty("id").GetGuid();

        // Create a version WITH a comment.
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", comment = "corrected the Q3 totals" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("v1")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        // The comment comes back on the version in the list.
        var version = (await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray()
            .Single(v => v.GetProperty("id").GetGuid() == versionId);
        Assert.Equal("corrected the Q3 totals", version.GetProperty("comment").GetString());
    }
}
