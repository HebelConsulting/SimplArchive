using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// GET /api/repositories/{id}/documents stamps its rows' capabilities (#889).
//
// It did not. Those rows implement ICarriesRowCapabilities, so they DECLARED canDelete, canEditIndexData,
// canMove, canManagePermissions and canCreateChildren — and nothing ever assigned them, so every row answered
// false. Per ADR 0543 a client reads false as "not available to you", which would have presented a repository's
// documents as entirely read-only to the person who owns them.
//
// Nothing caught it because no client follows this listing's `documents` rel yet, and because the interface can
// only make DECLARING the flags a compile error — never POPULATING them. This is the test that closes that gap
// for this endpoint; the sibling listings have their own coverage.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class RepositoryDocumentsCapabilityTests
{
    private readonly E2EApiFactory _factory;

    public RepositoryDocumentsCapabilityTests(E2EApiFactory factory) => _factory = factory;

    private static JsonElement RowFor(JsonElement listing, Guid id) =>
        listing.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task An_owner_is_told_what_it_may_do_to_each_listed_document()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var doc = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = $"d{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        var row = RowFor(await TestJson.Get(owner, $"/api/repositories/{repo}/documents"), doc);

        Assert.True(row.GetProperty("canDelete").GetBoolean());
        Assert.True(row.GetProperty("canEditIndexData").GetBoolean());
        Assert.True(row.GetProperty("canMove").GetBoolean());
        Assert.True(row.GetProperty("canManagePermissions").GetBoolean());

        // A plain folder admits a plain child, and this caller holds CanCreateSubItems — both halves true.
        Assert.True(row.GetProperty("canCreateChildren").GetBoolean());
    }

    [Fact]
    public async Task A_reader_is_told_it_may_do_none_of_them()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var doc = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = $"d{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        var readerEmail = $"docsreader-{Guid.NewGuid():N}@e2e.local";
        var readerId = await _factory.SeedUserAsync(tenantId, readerEmail, "read-1234", "Docs Reader");
        (await owner.PutAsJsonAsync($"/api/documents/{repo}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true }))
            .EnsureSuccessStatusCode();
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, "read-1234"));

        var row = RowFor(await TestJson.Get(reader, $"/api/repositories/{repo}/documents"), doc);

        // The half that makes the test above mean something: these are false because the caller lacks the
        // rights, not because nothing stamps them. Before the fix BOTH tests would have seen false here.
        Assert.False(row.GetProperty("canDelete").GetBoolean());
        Assert.False(row.GetProperty("canEditIndexData").GetBoolean());
        Assert.False(row.GetProperty("canMove").GetBoolean());
        Assert.False(row.GetProperty("canManagePermissions").GetBoolean());
        Assert.False(row.GetProperty("canCreateChildren").GetBoolean());
    }
}
