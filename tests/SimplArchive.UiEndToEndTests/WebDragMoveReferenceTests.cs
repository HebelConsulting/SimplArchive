using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Internal move/reference drag-drop in the Repositories tab (ADR "Desktop drag-and-drop move and reference",
// web parity): dragging a list row (or the whole multi-selection) onto a folder opens a Move/Reference prompt
// and moves — or references — all of them. Playwright can't originate a real HTML5 drag, so the drop is
// simulated with a synthetic DataTransfer carrying our custom node MIME (the same technique as the file-drop
// filing test), which exercises the real dropUpload.js → PerformNodeDropAsync path.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebDragMoveReferenceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDragMoveReferenceTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dragging_the_multi_selection_onto_a_folder_moves_all_of_them()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var targetName = $"drag-target-{suffix}";
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = targetName })).EnsureSuccessStatusCode();
        var docNames = new[] { $"drag-{suffix}-a", $"drag-{suffix}-b" };
        foreach (var name in docNames)
        {
            (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).EnsureSuccessStatusCode();
        }

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var rowA = list.Locator(".wb-list-row").Filter(new() { HasText = docNames[0] });
        var rowB = list.Locator(".wb-list-row").Filter(new() { HasText = docNames[1] });
        var targetRow = list.Locator(".wb-list-row").Filter(new() { HasText = targetName });
        await Expect(rowA).ToBeVisibleAsync();
        await Expect(targetRow).ToBeVisibleAsync();

        // Ctrl-click both document rows → the multi-selection (and its bulk bar) forms.
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        await rowA.DispatchEventAsync("click", ctrl);
        await rowB.DispatchEventAsync("click", ctrl);
        await Expect(list.Locator(".wb-bulk-bar")).ToContainTextAsync("2 selected");

        // Simulate dropping the grabbed row (which is in the selection) onto the target folder: a DataTransfer
        // carrying "<id>|false" under the node MIME. Because the id is part of the ≥2 selection, the WHOLE set moves.
        var draggedId = await rowA.GetAttributeAsync("data-node-id");
        var dataTransfer = await page.EvaluateHandleAsync(
            @"(payload) => { const dt = new DataTransfer(); dt.setData('application/x-simplarchive-node', payload); return dt; }",
            $"{draggedId}|false");
        await targetRow.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });

        // The Move/Reference prompt → choose Move.
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Move", Exact = true }).ClickAsync();

        // The bulk result snackbar confirms both were moved (0 skipped).
        await Expect(page.Locator(".mud-snackbar")).ToContainTextAsync("2 item(s) moved");

        // Both docs leave the root listing…
        await Expect(rowA).ToHaveCountAsync(0);
        await Expect(rowB).ToHaveCountAsync(0);

        // …and now live inside the target folder (open it and confirm).
        await targetRow.DblClickAsync();
        await Expect(list.GetByText(docNames[0]).First).ToBeVisibleAsync();
        await Expect(list.GetByText(docNames[1]).First).ToBeVisibleAsync();
    }
}
