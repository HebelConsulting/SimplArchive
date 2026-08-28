using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + MinIO, exercising document check-out / check-in (ADR "Document
// check-out / check-in"): a user takes the exclusive edit lock; while held, everyone else's content/metadata
// mutations are refused (409 DOCUMENT_CHECKED_OUT) and a second check-out is refused (409
// DOCUMENT_ALREADY_CHECKED_OUT); GET /api/checkouts lists the holder's locks with the current SHA-256;
// releasing frees it. A CanOverrideCheckout holder can force-release someone else's lock; a plain user can't.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class CheckoutTests
{
    private readonly E2EApiFactory _factory;

    public CheckoutTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Checkout_locks_the_document_for_everyone_else_until_released()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // The ServiceAccount owns a repo + a document with a confirmed version (full rights via the auto-grant).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"CO {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "locked-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "v1 content");

        // Two interactive users (tenant admins → ACL bypass, so they CanEditContent on the doc).
        var (aEmail, aClient) = await SeedAdminAsync(tenantId);
        var (_, bClient) = await SeedAdminAsync(tenantId);

        // User A checks out → 200, and the document now reports the lock (by A).
        (await aClient.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();
        var afterCheckout = await TestJson.Get(aClient, $"/api/documents/{docId}");
        Assert.True(afterCheckout.GetProperty("checkedOut").GetProperty("byMe").GetBoolean());

        // While held by A: the ServiceAccount owner and user B are both refused a new version (409).
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bClient.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).StatusCode);

        // A second check-out (by B) is refused with 409 ALREADY_CHECKED_OUT; the ServiceAccount can't check out at all.
        Assert.Equal(HttpStatusCode.Conflict, (await bClient.PutAsync($"/api/documents/{docId}/checkout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync($"/api/documents/{docId}/checkout", null)).StatusCode);

        // GET /api/checkouts (as A) lists the doc with the current version's SHA-256.
        var myCheckouts = await TestJson.Get(aClient, "/api/checkouts");
        var item = myCheckouts.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId);
        Assert.False(string.IsNullOrEmpty(item.GetProperty("sha256").GetString()));

        // The holder can still add a new version (the check-in upload happens while the lock is held).
        await UploadConfirmedVersionAsync(aClient, docId, "v2 edited by holder");

        // A releases (check-in) → 204; afterwards the owner can mutate again and the checkouts list is empty.
        Assert.Equal(HttpStatusCode.NoContent, (await aClient.DeleteAsync($"/api/documents/{docId}/checkout")).StatusCode);
        Assert.Null(GetCheckedOut(await TestJson.Get(owner, $"/api/documents/{docId}")));
        (await owner.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).EnsureSuccessStatusCode();
        Assert.Empty((await TestJson.Get(aClient, "/api/checkouts")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Override_force_releases_another_users_checkout()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"OV {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "override-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "content");

        var (_, holder) = await SeedAdminAsync(tenantId);
        var (bystanderEmail, bystander) = await SeedAdminAsync(tenantId); // a tenant admin, but no CanOverrideCheckout

        // An admin with CanOverrideCheckout.
        var overriderEmail = $"ov-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, overriderEmail, "over-1234", "Overrider");
        await _factory.GrantCanOverrideCheckoutAsync(overriderEmail);
        using var overrider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(overriderEmail, "over-1234"));

        // The holder checks out.
        (await holder.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        // A tenant admin WITHOUT CanOverrideCheckout can't release someone else's lock (403).
        Assert.Equal(HttpStatusCode.Forbidden, (await bystander.DeleteAsync($"/api/documents/{docId}/checkout")).StatusCode);

        // The CanOverrideCheckout holder force-releases it (204) → the document is free again.
        Assert.Equal(HttpStatusCode.NoContent, (await overrider.DeleteAsync($"/api/documents/{docId}/checkout")).StatusCode);
        Assert.Null(GetCheckedOut(await TestJson.Get(owner, $"/api/documents/{docId}")));
    }

    [Fact]
    public async Task Working_copy_stash_survives_and_is_cleared_on_release()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"ST {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "stash-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "original");

        var (_, holder) = await SeedAdminAsync(tenantId);
        await holder.PutAsync($"/api/documents/{docId}/checkout", null);

        // No stash yet — not modified (nothing to check in).
        var before = (await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId);
        Assert.False(before.GetProperty("hasStash").GetBoolean());
        Assert.False(before.GetProperty("isModified").GetBoolean());

        // Save to cloud: get a presigned PUT and upload the in-progress working copy.
        var uploadUrl = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        using var storage = new HttpClient();
        var wip = "work in progress edits";
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(Encoding.UTF8.GetBytes(wip)))).EnsureSuccessStatusCode();

        // Now the check-out reports a stash + a download URL whose bytes match what was uploaded.
        var after = (await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId);
        Assert.True(after.GetProperty("hasStash").GetBoolean());
        // The stash ("work in progress edits") differs from the version ("original") → modified (ADR 0513).
        Assert.True(after.GetProperty("isModified").GetBoolean());
        var downloadUrl = after.GetProperty("stashDownloadUrl").GetString()!;
        Assert.Equal(wip, await storage.GetStringAsync(downloadUrl));

        // Releasing (check-in / unlock) clears the stash.
        (await holder.DeleteAsync($"/api/documents/{docId}/checkout")).EnsureSuccessStatusCode();
        await holder.PutAsync($"/api/documents/{docId}/checkout", null); // re-check-out → stash should be gone
        var rechecked = (await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId);
        Assert.False(rechecked.GetProperty("hasStash").GetBoolean());

        // A non-holder can't stash a working copy.
        var (_, bystander) = await SeedAdminAsync(tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await bystander.PostAsJsonAsync($"/api/checkouts/{docId}/working-copy", new { })).StatusCode);
    }

    [Fact]
    public async Task Check_in_from_stash_promotes_it_to_a_new_version_and_clears_it()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"CI {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "webcheckin-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "v1");

        var (_, holder) = await SeedAdminAsync(tenantId);
        await holder.PutAsync($"/api/documents/{docId}/checkout", null);

        // Check in with no stash → 400 NO_STASH.
        Assert.Equal(HttpStatusCode.BadRequest, (await holder.PostAsJsonAsync($"/api/checkouts/{docId}/checkin", new { })).StatusCode);

        // Upload the edited working copy to the stash, then check in from it.
        var uploadUrl = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        using var storage = new HttpClient();
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(Encoding.UTF8.GetBytes("web-edited v2")))).EnsureSuccessStatusCode();
        (await holder.PostAsJsonAsync($"/api/checkouts/{docId}/checkin", new { })).EnsureSuccessStatusCode();

        // The lock is released, the stash is gone (no litter), and the latest version is the edited content.
        Assert.Null(GetCheckedOut(await TestJson.Get(owner, $"/api/documents/{docId}")));
        Assert.Empty((await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(await _factory.ListObjectKeysAsync($"tenants/{tenantId}/users/"), k => k.EndsWith(docId.ToString()));

        var versions = await TestJson.Get(owner, $"/api/documents/{docId}/versions");
        var latest = versions.GetProperty("versions").EnumerateArray().OrderByDescending(v => v.GetProperty("versionNumber").GetInt32()).First();
        var download = latest.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        Assert.Equal("web-edited v2", await storage.GetStringAsync(download));
    }

    // Anti-litter (like the intray, ADR 0306): the S3 checkout stash prefix must not accumulate orphaned working
    // copies — a release (check-in / unlock / discard / override) removes the stash object.
    [Fact]
    public async Task Releasing_a_checkout_leaves_no_stash_litter_in_object_storage()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"LT {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "litter-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "original");

        var checkoutPrefix = $"tenants/{tenantId}/users/";
        Assert.DoesNotContain(await _factory.ListObjectKeysAsync(checkoutPrefix), k => k.EndsWith(docId.ToString()));

        var (_, holder) = await SeedAdminAsync(tenantId);

        // Check out + Save to cloud → exactly one stash object appears for this document.
        await holder.PutAsync($"/api/documents/{docId}/checkout", null);
        var uploadUrl = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        using var storage = new HttpClient();
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(Encoding.UTF8.GetBytes("wip")))).EnsureSuccessStatusCode();
        Assert.Contains(await _factory.ListObjectKeysAsync(checkoutPrefix), k => k.EndsWith(docId.ToString()));

        // Release (check-in / unlock) → the stash object is gone; the checkout prefix holds no litter for it.
        (await holder.DeleteAsync($"/api/documents/{docId}/checkout")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await _factory.ListObjectKeysAsync(checkoutPrefix), k => k.EndsWith(docId.ToString()));

        // Same again, but released by OVERRIDE — the (different) releasing user still clears the holder's stash.
        await holder.PutAsync($"/api/documents/{docId}/checkout", null);
        var uploadUrl2 = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        (await storage.PutAsync(uploadUrl2, new ByteArrayContent(Encoding.UTF8.GetBytes("wip2")))).EnsureSuccessStatusCode();
        Assert.Contains(await _factory.ListObjectKeysAsync(checkoutPrefix), k => k.EndsWith(docId.ToString()));

        var overriderEmail = $"lt-ov-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, overriderEmail, "over-1234", "Overrider");
        await _factory.GrantCanOverrideCheckoutAsync(overriderEmail);
        using var overrider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(overriderEmail, "over-1234"));
        (await overrider.DeleteAsync($"/api/documents/{docId}/checkout")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await _factory.ListObjectKeysAsync(checkoutPrefix), k => k.EndsWith(docId.ToString()));
    }

    [Fact]
    public async Task Extend_resets_the_idle_timer_and_is_authorized_for_holder_and_override_admin()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"CX {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "extend-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "v1");

        // The holder (A) checks out and notes the initial CheckedOutAt.
        var (_, holder) = await SeedAdminAsync(tenantId);
        (await holder.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();
        var before = CheckedOutAt(await TestJson.Get(holder, "/api/checkouts"), docId);

        // The holder extends → 204, and the idle timer (CheckedOutAt) moved forward — the lock is retained.
        Assert.Equal(HttpStatusCode.NoContent, (await holder.PostAsync($"/api/checkouts/{docId}/extend", null)).StatusCode);
        var afterList = await TestJson.Get(holder, "/api/checkouts");
        Assert.True(CheckedOutAt(afterList, docId) > before, "extend should reset CheckedOutAt to now");
        Assert.Contains(afterList.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == docId); // still locked

        // A tenant admin WITHOUT CanOverrideCheckout can't extend someone else's lock (403).
        var (_, bystander) = await SeedAdminAsync(tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await bystander.PostAsync($"/api/checkouts/{docId}/extend", null)).StatusCode);

        // A CanOverrideCheckout admin can extend it (204).
        var overriderEmail = $"cx-ov-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, overriderEmail, "over-1234", "Overrider");
        await _factory.GrantCanOverrideCheckoutAsync(overriderEmail);
        using var overrider = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(overriderEmail, "over-1234"));
        Assert.Equal(HttpStatusCode.NoContent, (await overrider.PostAsync($"/api/checkouts/{docId}/extend", null)).StatusCode);

        // Once released, extending a not-checked-out document is 409 CHECKOUT_NOT_HELD.
        (await holder.DeleteAsync($"/api/documents/{docId}/checkout")).EnsureSuccessStatusCode();
        var notHeld = await holder.PostAsync($"/api/checkouts/{docId}/extend", null);
        Assert.Equal(HttpStatusCode.Conflict, notHeld.StatusCode);
        Assert.Equal("CHECKOUT_NOT_HELD", JsonSerializer.Deserialize<JsonElement>(await notHeld.Content.ReadAsStringAsync()).GetProperty("errorCode").GetString());
    }

    private static DateTimeOffset CheckedOutAt(JsonElement checkouts, Guid docId) =>
        checkouts.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == docId).GetProperty("checkedOutAt").GetDateTimeOffset();

    [Fact]
    public async Task Compare_returns_a_unified_diff_of_the_working_copy_vs_the_current_version()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"CMP {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "compare-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "line one\nline two\nline three\n");

        var (_, holder) = await SeedAdminAsync(tenantId);
        await holder.PutAsync($"/api/documents/{docId}/checkout", null);

        // With no working copy stashed yet, compare is not available (nothing to diff against).
        Assert.False((await TestJson.Get(holder, $"/api/checkouts/{docId}/compare")).GetProperty("available").GetBoolean());

        // Stash an edited working copy (middle line changed).
        var uploadUrl = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        using var storage = new HttpClient();
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(Encoding.UTF8.GetBytes("line one\nline two CHANGED\nline three\n")))).EnsureSuccessStatusCode();

        // The holder's compare now shows a unified diff: the changed line as a removed + added pair.
        var cmp = await TestJson.Get(holder, $"/api/checkouts/{docId}/compare");
        Assert.True(cmp.GetProperty("available").GetBoolean());
        var lines = cmp.GetProperty("lines").EnumerateArray().ToList();
        Assert.Contains(lines, l => l.GetProperty("op").GetInt32() == 2 && l.GetProperty("text").GetString()!.Contains("line two"));  // removed
        Assert.Contains(lines, l => l.GetProperty("op").GetInt32() == 1 && l.GetProperty("text").GetString()!.Contains("CHANGED"));   // added

        // A non-holder can't compare someone else's working copy.
        var (_, bystander) = await SeedAdminAsync(tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await bystander.GetAsync($"/api/checkouts/{docId}/compare")).StatusCode);
    }

    private async Task<(string Email, HttpClient Client)> SeedAdminAsync(Guid tenantId)
    {
        var email = $"co-{Guid.NewGuid():N}@e2e.local";
        const string password = "co-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Editor");
        await _factory.GrantTenantAdminAsync(email); // ACL bypass → CanEditContent on any document
        return (email, _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)));
    }

    private static async Task UploadConfirmedVersionAsync(HttpClient client, Guid docId, string content)
    {
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
    }

    // The document resource's `checkedOut` block, or null when the property is JSON null (not checked out).
    private static JsonElement? GetCheckedOut(JsonElement document) =>
        document.TryGetProperty("checkedOut", out var c) && c.ValueKind != JsonValueKind.Null ? c : null;
}
