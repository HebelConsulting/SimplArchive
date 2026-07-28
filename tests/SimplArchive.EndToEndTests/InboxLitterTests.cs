using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end guard against inbox "litter": the rendition/text-layout services cache derived artifacts
// (`<stem>.preview.*`, `<stem>.textlayout.json`) next to the source object, which for an inbox item lands in
// the inbox prefix. They must never show up in the inbox listing (ADR "Avoid inbox preview litter", 0280) and
// must be swept along with the item on delete (ADR "Inbox item classification + preview", 0279's
// PurgeItemArtifactsAsync). Uses a real User (the inbox is scoped to the token's userId) + Gotenberg (the .csv
// preview generates a real rendition).
[Collection(E2ECollection.Name)]
public class InboxLitterTests
{
    private readonly E2EApiFactory _factory;

    public InboxLitterTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Inbox_preview_artifacts_are_hidden_from_the_listing_and_purged_on_delete()
    {
        // Any tenant + a real logged-in User (the inbox needs a userId; a ServiceAccount has none). The inbox
        // is personal, so the user needs no ACL grants for upload/preview/delete.
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"inbox-{Guid.NewGuid():N}@e2e.local";
        const string password = "inbox1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Inbox User");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        const string name = "report.csv";
        var prefix = $"tenants/{tenantId}/users/{userId}/inbox/";

        // Upload a .csv into the inbox (previewing it needs an office→PDF rendition).
        var upload = await TestJson.Post(user, "/api/inbox", new { fileName = name });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("name,amount\nInvoice,42\n")))).EnsureSuccessStatusCode();
        }

        // The listing shows exactly the one item.
        Assert.Equal([name], await InboxNamesAsync(user));

        // Previewing (and text-layout) caches derived artifacts in the inbox prefix.
        (await user.GetAsync($"/api/inbox/{name}/preview")).EnsureSuccessStatusCode();
        await user.GetAsync($"/api/inbox/{name}/text-layout"); // best-effort — may add a .textlayout.json

        // The litter really was written into the inbox prefix...
        var afterPreview = await _factory.ListObjectKeysAsync(prefix);
        Assert.Contains(afterPreview, k => k.Contains(".preview.", StringComparison.OrdinalIgnoreCase));

        // ...but the listing still shows only the real item (ADR 0280 filters the derived artifacts out).
        Assert.Equal([name], await InboxNamesAsync(user));

        // Deleting the item purges it AND every derived artifact (ADR 0279) — no orphans left in the prefix.
        (await user.DeleteAsync($"/api/inbox/{name}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await _factory.ListObjectKeysAsync(prefix), k => k != prefix);
    }

    private static async Task<string[]> InboxNamesAsync(HttpClient user) =>
        (await TestJson.Get(user, "/api/inbox")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToArray();
}
