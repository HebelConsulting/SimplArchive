using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of notification click-through (ADR "Notification viewer + click-through"). Setup over the API: a
// second user comments on the demo admin's document, so the admin gets a CommentPosted notification carrying the
// document's parent folder. The browser then opens the bell and clicks the notification, which navigates the
// workbench to the document.
[Collection(UiCollection.Name)]
public class WebNotificationClickThroughTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNotificationClickThroughTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Clicking_a_notification_navigates_to_its_document()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var docName = $"notif-doc-{suffix}";

        using var admin = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // The demo admin owns a repository + document.
        var repoId = await PostIdAsync(admin, "/api/repositories", new { name = $"notif-repo-{suffix}" });
        var docId = await PostIdAsync(admin, $"/api/documents/{repoId}/children", new { name = docName });

        // A second user (tenant admin, so they can see + comment) posts a comment → the admin gets a notification.
        var otherEmail = $"commenter-{suffix}@example.test";
        var otherId = await PostIdAsync(admin, "/api/users", new { email = otherEmail, displayName = $"Commenter {suffix}" });
        (await admin.PutAsJsonAsync($"/api/users/{otherId}/rights", TenantAdminRights)).EnsureSuccessStatusCode();
        var password = (await PostAsync(admin, $"/api/users/{otherId}/reset-password", new { })).GetProperty("password").GetString()!;

        using var other = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl, otherEmail, password));
        (await other.PostAsJsonAsync($"/api/documents/{docId}/comments", new { body = "please review" })).EnsureSuccessStatusCode();

        // Wait until the admin's notification (with the parent) is queryable.
        await Eventually(async () =>
        {
            var notes = (await admin.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("notifications").EnumerateArray();
            Assert.Contains(notes, n => n.TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.String && d.GetGuid() == docId);
        });

        // Browser: open the bell, click the notification → the workbench navigates to the document.
        var page = await Ui.LoginAsync(_app);
        await page.Locator("button[title='Notifications']").ClickAsync();
        await page.GetByText(docName, new() { Exact = false }).First.ClickAsync();

        // The document is now shown in the Repositories workbench (selected in its folder).
        await Expect(page.Locator(".wb-cols").GetByText(docName).First).ToBeVisibleAsync();
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

    private static async Task Eventually(Func<Task> assertion)
    {
        for (var i = 0; i < 30; i++)
        {
            try { await assertion(); return; }
            catch (Xunit.Sdk.XunitException) { await Task.Delay(500); }
        }

        await assertion();
    }
}
