using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of duplicate detection (ADR "Duplicate document detection"): uploading a file whose content is
// identical to an existing document pops the reference/file-anyway/cancel modal; "File it again anyway" proceeds.
[Collection(UiCollection.Name)]
public class WebDuplicateDetectionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDuplicateDetectionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Uploading_an_identical_file_prompts_the_duplicate_modal()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var content = $"web duplicate content {tag}\n";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Seed an existing document with the content in the demo repository.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var srcId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = $"dupsrc-{tag}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{srcId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{srcId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();

        // In the UI, upload a file with the SAME content → the duplicate modal appears.
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var newName = $"dupnew-{tag}";
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = newName + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes(content) });

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByText("This file already exists")).ToBeVisibleAsync();
        await Expect(dialog.GetByText($"dupsrc-{tag}")).ToBeVisibleAsync(); // the existing duplicate is listed

        // "File it again anyway" → the new document is uploaded and appears in the list.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File it again anyway" }).ClickAsync();
        await Expect(list.GetByText(newName)).ToBeVisibleAsync();
    }
}
