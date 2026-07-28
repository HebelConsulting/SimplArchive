using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the Manage-access ACL UI (ADR "Manage-access UI for document/folder ACLs"): the demo admin
// opens a throwaway document's Manage-access dialog from the detail pane and grants a fresh user the Viewer
// role; the new grant row (name + role) then shows in the dialog. A throwaway document + user keep the shared
// demo tenant untouched.
[Collection(UiCollection.Name)]
public class WebManageAccessTests
{
    private readonly SelfHostedAppFixture _app;

    public WebManageAccessTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Grant_a_user_viewer_access_from_the_detail_pane()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"acl-{suffix}";
        var granteeName = $"Grantee {suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("acl content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        // A fresh active user to grant access to — it shows up in the grantable-principals picker by display name.
        (await http.PostAsJsonAsync("/api/users", new { email = $"grantee-{suffix}@simplarchive.local", displayName = granteeName })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        // The Manage-access button is shown in the detail pane (the demo admin has CanManagePermissions via the
        // IsTenantAdmin ACL bypass). Open the dialog.
        var detail = page.Locator("[data-pane='index']");
        await detail.GetByRole(AriaRole.Button, new() { Name = "Manage access" }).ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Add access → pick the grantee in the principal picker → keep the default Viewer preset → Save.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add access" }).ClickAsync();
        await dialog.Locator(".mud-input-control").First.ClickAsync(); // MudSelect opens via its input-control
        await page.Locator(".mud-list-item").Filter(new() { HasText = granteeName }).First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        // The new grant row appears with the grantee's name and the Viewer role summary.
        await Expect(dialog.GetByText(granteeName).First).ToBeVisibleAsync();
        await Expect(dialog.GetByText("Viewer").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Break_inheritance_from_the_dialog_flips_the_toggle()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"inh-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("inh content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = page.Locator("[data-pane='list']").Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        var detail = page.Locator("[data-pane='index']");
        await detail.GetByRole(AriaRole.Button, new() { Name = "Manage access" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        // A fresh child inherits — the toggle offers Break inheritance. Click it and confirm.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Break inheritance" }).ClickAsync();
        await page.Locator(".mud-message-box").GetByRole(AriaRole.Button, new() { Name = "Break inheritance" }).ClickAsync();

        // The dialog reloads: the toggle now offers Restore inheritance (inheritance is broken).
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Restore inheritance" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Effective_access_expander_lists_who_can_access()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"eff-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("eff content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = page.Locator("[data-pane='list']").Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        var detail = page.Locator("[data-pane='index']");
        await detail.GetByRole(AriaRole.Button, new() { Name = "Manage access" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog).ToBeVisibleAsync();

        // Expand the Effective access panel — the demo admin resolves as a tenant admin.
        await dialog.GetByText("Effective access").ClickAsync();
        await Expect(dialog.GetByText("Tenant admin").First).ToBeVisibleAsync();
    }
}
