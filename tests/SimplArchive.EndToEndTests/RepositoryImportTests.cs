using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + MinIO (ADR "Repository import"): a tenant admin exports a subtree,
// re-imports the archive as a new repository, and the recreated document's version downloads byte-for-byte. A
// non-admin is refused.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class RepositoryImportTests
{
    private readonly E2EApiFactory _factory;

    public RepositoryImportTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_admin_imports_an_exported_archive_as_a_new_repository()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "import-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Source: repo → Folder → Report (a confirmed version with known bytes).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Src {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Folder" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "Report" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var payload = Encoding.UTF8.GetBytes($"import-content-{Guid.NewGuid():N}");
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(payload))).EnsureSuccessStatusCode();
        }
        var versionId = created.GetProperty("id").GetGuid();
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        // A markup annotation (a highlight shape) on the version travels with the archive (ADR "Annotations in
        // export/import").
        await TestJson.Post(owner, $"/api/documents/{docId}/versions/{versionId}/annotations",
            new { pageIndex = 0, kind = 1, positionX = 0.1, positionY = 0.2, width = 0.3, height = 0.05, text = "", color = "#FFEB3B" });

        // Export → archive bytes.
        var zip = await (await admin.GetAsync($"/api/documents/{repoId}/export?versions=all")).Content.ReadAsByteArrayAsync();

        // A non-admin can't import.
        using (var refused = MultipartOf(zip))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/repositories/import", refused)).StatusCode);
        }

        // Import as a new repository.
        JsonElement result;
        using (var content = MultipartOf(zip))
        {
            var response = await admin.PostAsync("/api/repositories/import", content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode}: {body}");
            }

            result = JsonSerializer.Deserialize<JsonElement>(body);
        }

        var newRootId = result.GetProperty("rootId").GetGuid();
        Assert.Equal(3, result.GetProperty("documents").GetInt32());
        Assert.Equal(1, result.GetProperty("versions").GetInt32());
        Assert.NotEqual(repoId, newRootId); // a fresh repository, not the source

        // Navigate the imported tree and download the recreated version — the bytes round-trip.
        var importedFolder = (await TestJson.Get(admin, $"/api/documents/{newRootId}/children")).GetProperty("children")[0].GetProperty("id").GetGuid();
        var importedReport = (await TestJson.Get(admin, $"/api/documents/{importedFolder}/children")).GetProperty("children")[0].GetProperty("id").GetGuid();
        var versions = await TestJson.Get(admin, $"/api/documents/{importedReport}/versions");
        var downloadUrl = versions.GetProperty("versions")[0].GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;

        using var dl = new HttpClient();
        Assert.Equal(payload, await (await dl.GetAsync(downloadUrl)).Content.ReadAsByteArrayAsync());

        // The annotation was recreated on the imported version (ADR "Annotations in export/import").
        var importedVersionId = versions.GetProperty("versions")[0].GetProperty("id").GetGuid();
        var annotations = await TestJson.Get(admin, $"/api/documents/{importedReport}/versions/{importedVersionId}/annotations");
        var annotation = annotations.GetProperty("annotations").EnumerateArray().Single();
        Assert.Equal(1, annotation.GetProperty("kind").GetInt32());
        Assert.Equal(0.3, annotation.GetProperty("width").GetDouble(), 3);
    }

    [Fact]
    public async Task Re_importing_the_same_archive_is_idempotent()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "reimport-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Idem {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Doc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        var zip = await (await admin.GetAsync($"/api/documents/{repoId}/export?versions=all")).Content.ReadAsByteArrayAsync();

        JsonElement First, Second;
        using (var c = MultipartOf(zip))
        {
            First = await (await admin.PostAsync("/api/repositories/import", c)).Content.ReadFromJsonAsync<JsonElement>();
        }
        using (var c = MultipartOf(zip))
        {
            Second = await (await admin.PostAsync("/api/repositories/import", c)).Content.ReadFromJsonAsync<JsonElement>();
        }

        // The second import matched the same root by origin — no duplicate, everything skipped.
        Assert.Equal(First.GetProperty("rootId").GetGuid(), Second.GetProperty("rootId").GetGuid());
        Assert.True(Second.GetProperty("skipped").GetInt32() >= 2);
        Assert.Equal(0, Second.GetProperty("versions").GetInt32());
    }

    [Fact]
    public async Task Acl_round_trips_when_permissions_are_included()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "acl-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var granteeId = await _factory.SeedUserAsync(tenantId, $"grantee-{Guid.NewGuid():N}@e2e.local", "pw-1234", "Grantee");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Acl {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        // The service-account owner holds CanManagePermissions on the repo it created (auto-grant, ADR 0197).
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{granteeId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        // Export + import with permissions on.
        var zip = await (await admin.GetAsync($"/api/documents/{repoId}/export?versions=all&includePermissions=true")).Content.ReadAsByteArrayAsync();
        JsonElement imported;
        using (var c = MultipartOf(zip))
        {
            imported = await (await admin.PostAsync("/api/repositories/import?includePermissions=true", c)).Content.ReadFromJsonAsync<JsonElement>();
        }

        // The imported root carries the grant to the same user (matched by email within the tenant).
        var acl = await TestJson.Get(admin, $"/api/documents/{imported.GetProperty("rootId").GetGuid()}/acl-entries");
        var grant = Assert.Single(acl.GetProperty("entries").EnumerateArray(), e => e.GetProperty("principalId").GetGuid() == granteeId);
        Assert.True(grant.GetProperty("canSee").GetBoolean());
        Assert.True(grant.GetProperty("canReadContent").GetBoolean());
    }

    [Fact]
    public async Task Merge_import_overlays_a_same_named_folder()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "merge-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Destination repo already has a "Shared" folder with a document.
        var destId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Dest {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var destSharedId = (await TestJson.Post(owner, $"/api/documents/{destId}/children", new { name = "Shared" })).GetProperty("id").GetGuid();
        await TestJson.Post(owner, $"/api/documents/{destSharedId}/children", new { name = "DestDoc" });

        // Source: a separate "Shared" folder (the export root) with a document + a confirmed version.
        var srcRepoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Src {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var srcSharedId = (await TestJson.Post(owner, $"/api/documents/{srcRepoId}/children", new { name = "Shared" })).GetProperty("id").GetGuid();
        var srcDocId = (await TestJson.Post(owner, $"/api/documents/{srcSharedId}/children", new { name = "SrcDoc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{srcDocId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("s")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{srcDocId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // Export the source "Shared" folder, then merge it into the destination.
        var zip = await (await admin.GetAsync($"/api/documents/{srcSharedId}/export?versions=all")).Content.ReadAsByteArrayAsync();
        using (var c = MultipartOf(zip))
        {
            (await admin.PostAsync($"/api/documents/{destId}/import?merge=true", c)).EnsureSuccessStatusCode();
        }

        // The destination still has exactly one "Shared" (the pre-existing one), now holding both documents.
        var destChildren = (await TestJson.Get(admin, $"/api/documents/{destId}/children")).GetProperty("children").EnumerateArray()
            .Where(e => e.GetProperty("name").GetString() == "Shared").ToList();
        Assert.Equal(destSharedId, Assert.Single(destChildren).GetProperty("id").GetGuid());

        var sharedNames = (await TestJson.Get(admin, $"/api/documents/{destSharedId}/children")).GetProperty("children").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "DestDoc", "SrcDoc" }, sharedNames);
    }

    [Fact]
    public async Task Merge_import_adds_a_new_version_to_a_same_named_document()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "leaf-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        async Task<Guid> AddConfirmedDocAsync(Guid parentId, string name, string content)
        {
            var docId = (await TestJson.Post(owner, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
            var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
            }
            await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
            return docId;
        }

        // Destination: "Dest" / "Shared" / "CommonDoc" (one confirmed version).
        var destId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Dest {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var destSharedId = (await TestJson.Post(owner, $"/api/documents/{destId}/children", new { name = "Shared" })).GetProperty("id").GetGuid();
        var destDocId = await AddConfirmedDocAsync(destSharedId, "CommonDoc", "dest-content");

        // Source: a separate "Shared" / "CommonDoc" (different content).
        var srcRepoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Src {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var srcSharedId = (await TestJson.Post(owner, $"/api/documents/{srcRepoId}/children", new { name = "Shared" })).GetProperty("id").GetGuid();
        await AddConfirmedDocAsync(srcSharedId, "CommonDoc", "src-content");

        // Merge into "Dest" with leafConflict=newVersion → the incoming CommonDoc is appended as a new version.
        var zip = await (await admin.GetAsync($"/api/documents/{srcSharedId}/export?versions=all")).Content.ReadAsByteArrayAsync();
        using (var c = MultipartOf(zip))
        {
            (await admin.PostAsync($"/api/documents/{destId}/import?merge=true&leafConflict=newVersion", c)).EnsureSuccessStatusCode();
        }

        // Still exactly one "CommonDoc" under the shared folder…
        var docs = (await TestJson.Get(admin, $"/api/documents/{destSharedId}/children")).GetProperty("children").EnumerateArray()
            .Where(e => e.GetProperty("name").GetString() == "CommonDoc").ToList();
        Assert.Single(docs);

        // …now with two versions (the original + the appended one).
        var versions = (await TestJson.Get(admin, $"/api/documents/{destDocId}/versions")).GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
    }

    // ADR "Classification in export/import": a document's sensitivity label rides the archive (always, as document
    // metadata) and is reassigned on import over real Postgres.
    [Fact]
    public async Task Sensitivity_label_round_trips_through_export_import()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "class-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var confidentialId = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Confidential").GetProperty("id").GetGuid();

        // Source: repo → Report (a confirmed version), labelled Confidential.
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Cls {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Report" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("classified")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/sensitivity", new { labelId = confidentialId })).EnsureSuccessStatusCode();

        var zip = await (await admin.GetAsync($"/api/documents/{repoId}/export?versions=all")).Content.ReadAsByteArrayAsync();

        JsonElement result;
        using (var content = MultipartOf(zip))
        {
            var response = await admin.PostAsync("/api/repositories/import", content);
            response.EnsureSuccessStatusCode();
            result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }

        // The imported document carries the Confidential label (resolved by name in the same tenant's catalog).
        var newRootId = result.GetProperty("rootId").GetGuid();
        var importedReport = (await TestJson.Get(admin, $"/api/documents/{newRootId}/children")).GetProperty("children")[0].GetProperty("id").GetGuid();
        var doc = await TestJson.Get(admin, $"/api/documents/{importedReport}");
        Assert.Equal("Confidential", doc.GetProperty("sensitivityLabelName").GetString());
    }

    private static MultipartFormDataContent MultipartOf(byte[] zip)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "import.zip");
        return content;
    }
}
