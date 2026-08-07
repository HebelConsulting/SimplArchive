using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352; recoloured and the
// criterion corrected in issue #376): a folder with nothing inside gets a pale-yellow glyph so it's spottable
// without expanding. The distinction that matters is HasChildren ("is ANYTHING filed here") vs HasSubfolders
// (the expander caret) — a folder holding only DOCUMENTS is a leaf in the folders-only tree but is NOT empty,
// and neither is one holding only REFERENCES, which is what #376 fixed.
[Collection(UiCollection.Name)]
public class WebEmptyFolderIconTests
{
    // .wb-tree-empty — #f2dd8c. Pale yellow since #376; it was a pastel blue. Deliberately much lighter than
    // the gold of a non-empty folder, since the two now share a hue.
    private const string EmptyTint = "rgb(242, 221, 140)";

    private readonly SelfHostedAppFixture _app;

    public WebEmptyFolderIconTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task An_empty_folder_is_tinted_and_a_documents_only_folder_is_not()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var emptyName = $"empty-{suffix}";
        var docsOnlyName = $"docs-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var emptyId = await CreateFolderAsync(http, repoId, emptyName);
        var docsOnlyId = await CreateFolderAsync(http, repoId, docsOnlyName);
        // One real DOCUMENT inside — no subfolder, so it stays a tree LEAF, but it is not empty.
        await AddDocumentAsync(http, docsOnlyId, $"doc-{suffix}");

        // A folder holding only a REFERENCE (issue #376). It has no child Document at all, which is exactly why
        // it used to be drawn as empty — the shortcut inside it went uncounted.
        var refsOnlyName = $"refs-{suffix}";
        var refsOnlyId = await CreateFolderAsync(http, repoId, refsOnlyName);
        var refTargetId = await CreateFolderAsync(http, repoId, $"target-{suffix}");
        (await http.PostAsJsonAsync($"/api/documents/{refsOnlyId}/references", new { targetId = refTargetId }))
            .EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var root = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();
        await root.Locator(".mud-treeview-item-arrow").ClickAsync();

        var emptyIcon = IconOf(tree, emptyName);
        var docsOnlyIcon = IconOf(tree, docsOnlyName);
        await Expect(emptyIcon).ToBeVisibleAsync(new() { Timeout = 15000 });

        Assert.Equal(EmptyTint, await ColorOfAsync(emptyIcon));
        Assert.NotEqual(EmptyTint, await ColorOfAsync(docsOnlyIcon));
        Assert.NotEqual(EmptyTint, await ColorOfAsync(IconOf(tree, refsOnlyName)));

        // Nothing leaks to the ancestor: the repository root holds these folders, so it is not empty.
        Assert.NotEqual(EmptyTint, await ColorOfAsync(root.Locator(".mud-treeview-item-icon > .mud-icon-root")));

        // Filing a document into the empty folder makes it non-empty on the next tree load.
        await AddDocumentAsync(http, emptyId, $"now-{suffix}");
        await page.ReloadAsync();
        await Expect(root).ToBeVisibleAsync(new() { Timeout = 15000 });
        await tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First
            .Locator(".mud-treeview-item-arrow").ClickAsync();
        await Expect(IconOf(tree, emptyName)).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.NotEqual(EmptyTint, await ColorOfAsync(IconOf(tree, emptyName)));
    }

    // The node's OWN icon — a MudTreeViewItem renders its children inside itself, so scope to the content row.
    private static ILocator IconOf(ILocator tree, string name) =>
        tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = name }).First
            .Locator(".mud-treeview-item-icon > .mud-icon-root");

    private static Task<string> ColorOfAsync(ILocator icon) =>
        icon.EvaluateAsync<string>("el => getComputedStyle(el).color");

    private static async Task<Guid> CreateFolderAsync(HttpClient http, Guid parentId, string name)
    {
        var created = await (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    // A child WITH a confirmed version is a document, not a folder — the parent gains HasChildren without
    // gaining HasSubfolders, which is exactly the case the icon must not mistake for "empty".
    private static async Task AddDocumentAsync(HttpClient http, Guid parentId, string name)
    {
        var docId = await CreateFolderAsync(http, parentId, name);
        var version = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
    }
}
