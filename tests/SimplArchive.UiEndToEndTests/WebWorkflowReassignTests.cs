using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of workflow review reassignment (ADR "Workflow review reassignment"). Setup is done over the API
// (create a document + version, submit it to reviewer R1), then the browser drives the web-only piece: the
// Users & groups tab refuses to deactivate R1 (who holds a pending review) and pops the replacement-reviewer
// dialog, where picking R2 reassigns the review and deactivates R1. Uses fresh users so it never touches the
// shared demo document's workflow (which WebWorkflowTests mutates).
[Collection(UiCollection.Name)]
public class WebWorkflowReassignTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWorkflowReassignTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Deactivating_a_reviewer_with_pending_reviews_prompts_for_a_replacement()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var r1Name = $"Web R1 {suffix}";
        var r2Name = $"Web R2 {suffix}";

        // --- API setup: two users (R1 a valid reviewer via tenant-admin; R2 the replacement candidate) and a
        //     confirmed-version document whose workflow is submitted to R1. ---
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var r1 = await PostIdAsync(http, "/api/users", new { email = $"web-r1-{suffix}@example.test", displayName = r1Name });
        await http.PutAsJsonAsync($"/api/users/{r1}/rights", TenantAdminRights);
        await PostIdAsync(http, "/api/users", new { email = $"web-r2-{suffix}@example.test", displayName = r2Name });

        var repoId = await PostIdAsync(http, "/api/repositories", new { name = $"web-wf-{suffix}" });
        var docId = await PostIdAsync(http, $"/api/documents/{repoId}/children", new { name = $"web-wf-doc-{suffix}" });
        var created = await PostAsync(http, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("reassign me")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/workflow/submit", new { reviewerId = r1 })).EnsureSuccessStatusCode();

        // --- Browser: deactivate R1 → the replacement dialog → pick R2 → R1 is deactivated. ---
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab").Filter(new() { HasText = "Users & groups" }).First.ClickAsync();

        var r1Row = page.Locator(".wb-list-row").Filter(new() { HasText = r1Name });
        await r1Row.First.ClickAsync();
        await page.Locator("button[title='Delete']").ClickAsync();

        // The confirm message box → Deactivate.
        await page.GetByRole(AriaRole.Button, new() { Name = "Deactivate", Exact = true }).ClickAsync();

        // The replacement-reviewer dialog: pick R2, then Reassign & deactivate.
        var dialog = page.Locator(".mud-dialog").Filter(new() { HasText = "still holds pending review tasks" });
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator(".mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = r2Name }).First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Reassign & deactivate" }).ClickAsync();

        // R1's row now shows as inactive.
        await Expect(r1Row.GetByText("(inactive)")).ToBeVisibleAsync();
    }

    private static readonly object TenantAdminRights = new
    {
        isTenantAdmin = true,
        canImpersonate = false,
        canOverrideCheckout = false,
        canLegalHold = false,
        canManageClassification = false,
        canResetMfa = false,
        canManageRepositories = false,
        canManageMasks = false,
        canManageServiceAccounts = false,
        canManageUsers = false,
        canViewAuditLog = false,
    };

    private static async Task<JsonElement> PostAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PostIdAsync(HttpClient http, string url, object body) =>
        (await PostAsync(http, url, body)).GetProperty("id").GetGuid();
}
