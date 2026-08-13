using System.Net;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The Check-out tab's preview shows the WORKING COPY (ADR "Check-out tab shows what you are about to check in").
// The question that tab answers is "what am I about to check in?", so previewing the archived side would answer
// the opposite one — which is why the preview reads the stash, not the current version.
[Collection(E2ECollection.Name)]
public class CheckoutPreviewTests
{
    private readonly E2EApiFactory _factory;

    public CheckoutPreviewTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_preview_follows_the_working_copy_and_is_only_advertised_once_one_exists()
    {
        var (user, docId, _) = await ArrangeCheckedOutDocumentAsync("archived body");

        // 1) No stash yet → no `preview` rel. A rel that 404s is worse than no rel (ADR 0543): the client would
        //    offer a preview affordance the server cannot honour.
        Assert.DoesNotContain("preview", await CheckoutRelsAsync(user, docId));

        // …and the endpoint itself says "nothing to show" rather than falling back to the archived version.
        using (var early = await user.GetAsync($"/api/checkouts/{docId}/preview"))
        {
            Assert.Equal(HttpStatusCode.NoContent, early.StatusCode);
        }

        // 2) Save a working copy → the rel appears and the preview resolves.
        await SaveWorkingCopyAsync(user, docId, "FIRSTEDIT");
        Assert.Contains("preview", await CheckoutRelsAsync(user, docId));

        var first = await FetchPreviewBodyAsync(user, docId);
        Assert.Contains("FIRSTEDIT", first);
        Assert.DoesNotContain("archived body", first);

        // 3) THE POINT. Save again over the same stash key and the preview must follow. The rendition cache is
        //    keyed on the source path, and the stash is rewritten under a stable key on every save over WebDAV —
        //    so a cached preview here would serve the PREVIOUS edit: a wrong document, shown confidently.
        await SaveWorkingCopyAsync(user, docId, "SECONDEDIT");

        var second = await FetchPreviewBodyAsync(user, docId);
        Assert.Contains("SECONDEDIT", second);
        Assert.DoesNotContain("FIRSTEDIT", second);
    }

    [Fact]
    public async Task Only_the_lock_holder_can_see_the_working_copy()
    {
        var (holder, docId, tenantId) = await ArrangeCheckedOutDocumentAsync("archived body");
        await SaveWorkingCopyAsync(holder, docId, "PRIVATE DRAFT");

        // A second user in the same tenant, who does not hold the lock. A working copy is the holder's
        // unfinished work; it is not part of the archive until they check it in — a tenant admin's ACL bypass
        // is deliberately not enough, which is why this asserts against one.
        using var other = await SeedAdminAsync(tenantId);
        using var response = await other.GetAsync($"/api/checkouts/{docId}/preview");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<string> FetchPreviewBodyAsync(HttpClient user, Guid docId)
    {
        var preview = await TestJson.Get(user, $"/api/checkouts/{docId}/preview");
        var url = preview.GetProperty("previewUrl").GetString()!;

        // The preview URL is presigned against object storage, so it is fetched anonymously — the same way the
        // client renders it.
        using var anonymous = new HttpClient();
        return await anonymous.GetStringAsync(url);
    }

    private static async Task<HashSet<string>> CheckoutRelsAsync(HttpClient user, Guid docId)
    {
        var list = await TestJson.Get(user, "/api/checkouts");
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId);
        return row.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()!).ToHashSet();
    }

    // A document with a confirmed version, checked out by the returned client.
    private async Task<(HttpClient Holder, Guid DocumentId, Guid TenantId)> ArrangeCheckedOutDocumentAsync(string archivedContent)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"COP {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"wc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(archivedContent)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        var holder = await SeedAdminAsync(tenantId);
        (await holder.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();
        return (holder, docId, tenantId);
    }

    private async Task<HttpClient> SeedAdminAsync(Guid tenantId)
    {
        var email = $"cop-{Guid.NewGuid():N}@e2e.local";
        const string password = "cop-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Editor");
        await _factory.GrantTenantAdminAsync(email);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
    }

    private static async Task SaveWorkingCopyAsync(HttpClient user, Guid docId, string content)
    {
        var upload = await TestJson.Post(user, $"/api/checkouts/{docId}/working-copy", new { });
        using var storage = new HttpClient();
        (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!,
            new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
    }
}
