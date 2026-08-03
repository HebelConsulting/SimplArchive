using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of retention review-before-disposition (ADR "Retention review-before-disposition"). Setup is
// over the API (a 0-year-retention mask makes a document immediately overdue); the browser drives the
// Retention tab's Dispose action; the result is asserted over the API. The demo admin holds
// CanManageClassification, so the Retention tab + actions are available.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebRetentionDispositionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRetentionDispositionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Retention_tab_disposes_an_overdue_document()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var docName = $"ret-doc-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // A 0-year-retention mask → any document assigned it is immediately overdue.
        var maskId = await PostIdAsync(http, "/api/masks", new { name = $"ret-mask-{suffix}", retentionYears = 0, fields = Array.Empty<object>() });
        var repoId = await PostIdAsync(http, "/api/repositories", new { name = $"ret-{suffix}" });
        var docId = await PostIdAsync(http, $"/api/documents/{repoId}/children", new { name = docName });
        (await http.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Retention\"]").First.ClickAsync();

        // The overdue document's row shows a Dispose action; click it and confirm.
        var row = page.Locator("tr").Filter(new() { HasText = docName });
        await Expect(row.GetByText("Due for disposition")).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Dispose" }).ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Dispose" }).ClickAsync();

        // It's disposed to the recycle bin — GET now 404s.
        await Eventually(async () =>
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/documents/{docId}")).StatusCode));
    }

    private static async Task<Guid> PostIdAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("id").GetGuid();
    }

    private static async Task Eventually(Func<Task> assertion)
    {
        for (var i = 0; i < 20; i++)
        {
            try { await assertion(); return; }
            catch (Xunit.Sdk.XunitException) { await Task.Delay(250); }
        }

        await assertion();
    }
}
