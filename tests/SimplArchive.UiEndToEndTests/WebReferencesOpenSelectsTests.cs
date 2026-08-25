using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// References dialog "Open" bugfix: opening a referencing folder from the references dialog must open that folder
// AND select the document (its reference/shortcut row) for viewing — previously it only opened the folder. Drives
// the real dialog → NavigateToFolderAsync path in the browser.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebReferencesOpenSelectsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebReferencesOpenSelectsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Opening_a_referencing_folder_selects_the_document_for_viewing()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var docName = $"refopen-{suffix}";
        var refFolderName = $"reffolder-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = refFolderName })).EnsureSuccessStatusCode();
        var refFolderId = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}/children"))
            .GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == refFolderName).GetProperty("id").GetGuid();

        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");

        // Upload a document at the repo root.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = docName + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("body") });
        await Expect(list.Locator("[data-drop-doc]").Filter(new() { HasText = docName })).ToBeVisibleAsync();

        // Reference that document into the subfolder (via the API), then reload so the row exposes its References menu.
        var docId = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}/children"))
            .GetProperty("children").EnumerateArray().First(c => (c.GetProperty("name").GetString() ?? "").Contains(suffix) && c.GetProperty("hasVersions").GetBoolean()).GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{refFolderId}/references", new { targetId = docId })).EnsureSuccessStatusCode();

        await page.ReloadAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var docRow = list.Locator(".wb-list-row").Filter(new() { HasText = docName });
        await Expect(docRow).ToBeVisibleAsync();

        // Open the document's References dialog and click "Open" on the referencing folder.
        await docRow.Locator("button").Last.ClickAsync(); // the row's ⋮ menu
        await page.GetByText("References").First.ClickAsync(); // the "References…" menu item
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToContainTextAsync(refFolderName);
        await dialog.Locator(".ref-row").Filter(new() { HasText = refFolderName })
            .GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();

        // The referencing folder is now open, its reference (shortcut) row is selected, and the document loaded for
        // viewing (the index pane shows its current version).
        var refRow = list.Locator(".wb-list-row").Filter(new() { HasText = docName });
        await Expect(refRow).ToHaveClassAsync(new Regex("wb-list-row-selected"));
        await Expect(index.Locator(".wb-current-version")).ToHaveTextAsync("1");
    }
}
