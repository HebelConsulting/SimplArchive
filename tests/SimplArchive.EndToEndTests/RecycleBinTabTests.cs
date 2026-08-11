using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the tenant-wide recycle bin (ADR "Recycle bin tab"):
// GET /api/recycle-bin lists the deletion roots with path + deleted-by (from the audit trail); the read
// endpoints serve a soft-deleted document (so the detail pane can inspect it); a tenant admin empties the bin.
[Collection(E2ECollection.Name)]
public class RecycleBinTabTests
{
    private readonly E2EApiFactory _factory;

    public RecycleBinTabTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Lists_deletion_roots_with_path_and_deleted_by_and_empties()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "recycle-1234";
        var adminEmail = $"rbadmin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Recycle Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        var repoName = $"Recycle {Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "A folder" })).GetProperty("id").GetGuid();
        var childId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "a child" })).GetProperty("id").GetGuid();

        // The tenant admin (ACL bypass) deletes the folder — cascading to the child.
        var etag = (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{folderId}"))).Headers.ETag!.ToString();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{folderId}");
        del.Headers.TryAddWithoutValidation("If-Match", etag);
        (await admin.SendAsync(del)).EnsureSuccessStatusCode();

        // The tenant-wide recycle bin lists the folder with its path + deleted-by (from the audit event), and
        // the cascade-deleted child too (with its full path under the folder).
        var items = (await TestJson.Get(admin, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        var folderRow = items.Single(i => i.GetProperty("id").GetGuid() == folderId);
        Assert.Equal(repoName, folderRow.GetProperty("path").GetString());
        Assert.Equal("Recycle Admin", folderRow.GetProperty("deletedBy").GetString());
        var childRow = items.Single(i => i.GetProperty("id").GetGuid() == childId);
        Assert.Equal($"{repoName} / A folder", childRow.GetProperty("path").GetString());

        // The detail-pane read endpoints serve the soft-deleted document (so it can be inspected); GET
        // /documents/{id} itself stays 404 for a deleted item.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{folderId}/index-data")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{childId}/index-data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{folderId}")).StatusCode);

        // Empty the recycle bin → both are gone.
        (await admin.PostAsync("/api/recycle-bin/purge", null)).EnsureSuccessStatusCode();
        Assert.Empty((await TestJson.Get(admin, "/api/recycle-bin")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{folderId}/index-data")).StatusCode);
    }

    // Selecting a row in the recycle bin shows the deleted item's mask, index data, chat and versions — so the
    // row must ADVERTISE those four addresses, not leave the client to build them from the id (ADR 0543).
    //
    // They belong on the row rather than behind a `self` the client would fetch first: a listing's addresses
    // arrive with the listing and cost nothing, whereas a `self` hop would spend a request per selection to
    // learn four addresses already known when the list was built (ADR 0557).
    //
    // Each rel is FOLLOWED here, not just matched by name. A rel that is advertised but does not resolve is the
    // failure this guards against, and it is invisible to a test that only reads the link list — a missing rel
    // is meaningful in this API (it means "not available to you"), so a typo and a deliberate absence look
    // identical until something follows one.
    [Fact]
    public async Task A_recycle_bin_row_advertises_the_detail_addresses_the_pane_reads()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "recycle-rels-1234";
        var adminEmail = $"rbrels-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Recycle Rels Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"RecycleRels {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "a deleted doc" })).GetProperty("id").GetGuid();

        var etag = (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{docId}"))).Headers.ETag!.ToString();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}");
        del.Headers.TryAddWithoutValidation("If-Match", etag);
        (await admin.SendAsync(del)).EnsureSuccessStatusCode();

        var row = (await TestJson.Get(admin, "/api/recycle-bin")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == docId);

        foreach (var rel in new[] { "mask", "index-data", "chat", "versions", "restore", "purge" })
        {
            var href = row.GetProperty("links").EnumerateArray()
                .FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
                ? link.GetProperty("href").GetString()
                : null;

            Assert.True(href is not null, $"a recycle-bin row advertises no '{rel}' rel — the client would have to compose that URL (ADR 0543)");
        }

        // Follow the four read rels: each resolves for a SOFT-DELETED document, which is the whole point.
        foreach (var rel in new[] { "mask", "index-data", "chat", "versions" })
        {
            var href = row.GetProperty("links").EnumerateArray()
                .First(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

            var response = await admin.GetAsync(href);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"following '{rel}' → {href} returned {(int)response.StatusCode} {response.StatusCode}, expected 200. "
                + "The recycle bin's detail pane reads this for a SOFT-DELETED document; the client swallows the "
                + "failure as a partial detail, so a broken read here is invisible in the UI.");
        }
    }

    [Fact]
    public async Task A_deleted_document_the_caller_cannot_see_is_not_listed()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "recycle-1234";
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
        var outsiderEmail = $"outsider-{Guid.NewGuid():N}@e2e.local";
        var viewerId = await _factory.SeedUserAsync(tenantId, viewerEmail, password, "Viewer");
        await _factory.SeedUserAsync(tenantId, outsiderEmail, password, "Outsider");
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, password));
        using var outsider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(outsiderEmail, password));

        // A repo + document; the viewer is granted CanSee on the repo (so it inherits to the document), the
        // outsider gets nothing.
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Acl {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "secret-doc" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{viewerId}", new { canSee = true })).EnsureSuccessStatusCode();

        // Delete the document (the ServiceAccount owner has full rights via the repo auto-grant).
        var etag = (await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{docId}"))).Headers.ETag!.ToString();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}");
        del.Headers.TryAddWithoutValidation("If-Match", etag);
        (await owner.SendAsync(del)).EnsureSuccessStatusCode();

        // The viewer (CanSee) finds it in their recycle bin; the outsider (no CanSee) does not.
        var viewerItems = (await TestJson.Get(viewer, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(viewerItems, i => i.GetProperty("id").GetGuid() == docId);

        var outsiderItems = (await TestJson.Get(outsider, "/api/recycle-bin")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(outsiderItems, i => i.GetProperty("id").GetGuid() == docId);
    }

    [Fact]
    public async Task Bulk_restore_brings_back_selected_items_and_counts_skips()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "recycle-1234";
        var adminEmail = $"rbulk-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Bulk Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Bulk {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var aId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "doc-a" })).GetProperty("id").GetGuid();
        var bId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "doc-b" })).GetProperty("id").GetGuid();
        var cId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "doc-c" })).GetProperty("id").GetGuid();

        // Delete all three.
        foreach (var id in new[] { aId, bId, cId })
        {
            var etag = (await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{id}"))).Headers.ETag!.ToString();
            var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}");
            del.Headers.TryAddWithoutValidation("If-Match", etag);
            (await owner.SendAsync(del)).EnsureSuccessStatusCode();
        }

        // Bulk restore A + B (leave C). A bogus id + C-is-restored-later are covered separately; here the
        // response reports 2 restored, 0 skipped.
        var result = await TestJson.Post(admin, "/api/recycle-bin/restore", new { ids = new[] { aId, bId } });
        Assert.Equal(2, result.GetProperty("restored").GetInt32());
        Assert.Equal(0, result.GetProperty("skipped").GetInt32());

        // A + B are active again (GET 200); C is still in the recycle bin.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{aId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{bId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/documents/{cId}")).StatusCode);
        Assert.Contains((await TestJson.Get(admin, "/api/recycle-bin")).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == cId);

        // Re-restoring A (already active) + a bogus id → both skipped, none restored.
        var again = await TestJson.Post(admin, "/api/recycle-bin/restore", new { ids = new[] { aId, Guid.NewGuid() } });
        Assert.Equal(0, again.GetProperty("restored").GetInt32());
        Assert.Equal(2, again.GetProperty("skipped").GetInt32());

        // A caller without CanDelete can't restore: a plain user with no grants → the item is skipped.
        var plainEmail = $"rbulk-plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));
        var denied = await TestJson.Post(plain, "/api/recycle-bin/restore", new { ids = new[] { cId } });
        Assert.Equal(0, denied.GetProperty("restored").GetInt32());
        Assert.Equal(1, denied.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Bulk_purge_removes_selected_items_skips_protected_and_requires_admin()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "recycle-1234";
        var adminEmail = $"pbulk-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Purge Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Purge {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var aId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "pdoc-a" })).GetProperty("id").GetGuid();
        var bId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "pdoc-b" })).GetProperty("id").GetGuid();
        var cId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "pdoc-c" })).GetProperty("id").GetGuid();

        foreach (var id in new[] { aId, bId, cId })
        {
            var etag = (await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{id}"))).Headers.ETag!.ToString();
            var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}");
            del.Headers.TryAddWithoutValidation("If-Match", etag);
            (await owner.SendAsync(del)).EnsureSuccessStatusCode();
        }

        // A non-admin can't bulk-purge.
        var plainEmail = $"pbulk-plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/recycle-bin/purge-selected", new { ids = new[] { aId } })).StatusCode);

        // Purge A + B → 2 purged; both gone (read endpoints 404), C still in the bin.
        var result = await TestJson.Post(admin, "/api/recycle-bin/purge-selected", new { ids = new[] { aId, bId } });
        Assert.Equal(2, result.GetProperty("purged").GetInt32());
        Assert.Equal(0, result.GetProperty("skipped").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{aId}/index-data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{bId}/index-data")).StatusCode);
        Assert.Contains((await TestJson.Get(admin, "/api/recycle-bin")).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == cId);

        // Purging C + an active (not-deleted) repo + a bogus id → 1 purged (C), 2 skipped (active + gone).
        var mixed = await TestJson.Post(admin, "/api/recycle-bin/purge-selected", new { ids = new[] { cId, repoId, Guid.NewGuid() } });
        Assert.Equal(1, mixed.GetProperty("purged").GetInt32());
        Assert.Equal(2, mixed.GetProperty("skipped").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{repoId}")).StatusCode); // the active repo untouched
    }

    [Fact]
    public async Task Empty_requires_tenant_admin()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        const string password = "recycle-1234";
        var plainEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync("/api/recycle-bin/purge", null)).StatusCode);
    }
}
