using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The destructive half of ADR 0543 (#858). The create path was gated on `create-child`; Delete, Rename and Move
// were offered to anyone who could SEE a row and answered with a 403 the menu had promised would not come.
//
// The server now says which of the three it will accept, in the shape ADR 0719 dictates: DELETE and PUT live at
// the document's OWN address, so they are capability flags (a second rel there would be the same URL under
// another name), while Move has an address of its own and is therefore a rel.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DestructiveAffordanceGatingTests
{
    private readonly E2EApiFactory _factory;

    public DestructiveAffordanceGatingTests(E2EApiFactory factory) => _factory = factory;

    private static List<string> RelsOf(JsonElement resource) =>
        [.. resource.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];

    [Fact]
    public async Task A_full_rights_caller_is_told_it_may_delete_rename_and_move()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var doc = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = $"d{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        var resource = await TestJson.Get(owner, $"/api/documents/{doc}");

        Assert.True(resource.GetProperty("canDelete").GetBoolean());
        Assert.True(resource.GetProperty("canEditIndexData").GetBoolean());
        Assert.Contains("move", RelsOf(resource));
    }

    [Fact]
    public async Task A_reader_is_told_it_may_not_and_the_server_agrees()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var doc = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = $"d{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        // A reader: see and read, nothing else.
        var readerEmail = $"reader-{Guid.NewGuid():N}@e2e.local";
        var readerId = await _factory.SeedUserAsync(tenantId, readerEmail, "read-1234", "Reader");
        (await owner.PutAsJsonAsync($"/api/documents/{repo}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true }))
            .EnsureSuccessStatusCode();
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, "read-1234"));

        var resource = await TestJson.Get(reader, $"/api/documents/{doc}");

        Assert.False(resource.GetProperty("canDelete").GetBoolean());
        Assert.False(resource.GetProperty("canEditIndexData").GetBoolean());
        Assert.DoesNotContain("move", RelsOf(resource));

        // And the refusals are real — the flags are not decoration over an endpoint that would have allowed it.
        // This is the assertion that keeps a gate honest: a client trusts absence to mean "would be refused",
        // so a test that only checked the absence would pass just as well against a lying resource.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/documents/{doc}")).StatusCode);
    }

    [Fact]
    public async Task A_repository_root_may_be_moved_only_by_someone_who_may_demote_it()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        // Moving a ROOT demotes a repository, so the endpoint wants CanManageRepositories on top of CanMove.
        // The gate has to mirror that: withholding the rel from every root — the obvious shortcut, since a root
        // has no parent — would hide a legitimate action from exactly the people entitled to it.
        Assert.Contains("move", RelsOf(await TestJson.Get(owner, $"/api/documents/{repo}")));

        // A tenant admin holds every per-document right by bypass, but not the repository-management system
        // right, so this is the caller the shortcut would have been indistinguishable for.
        var adminEmail = $"tadmin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Tenant Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        Assert.DoesNotContain("move", RelsOf(await TestJson.Get(admin, $"/api/documents/{repo}")));
    }
}
