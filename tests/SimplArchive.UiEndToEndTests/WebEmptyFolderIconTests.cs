using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the empty-folder tree icon (ADR "Empty-folder tree icon", issue #352): a folder with nothing
// inside gets a pastel glyph so it's spottable without expanding. The distinction that matters is HasChildren
// (any child) vs HasSubfolders (the expander caret) — a folder holding only DOCUMENTS is a leaf in the
// folders-only tree but is NOT empty, so it must keep the normal glyph.
[Collection(UiCollection.Name)]
public class WebEmptyFolderIconTests
{
    private const string Pastel = "rgb(143, 180, 217)"; // .wb-tree-empty — #8fb4d9

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

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var root = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First;
        await Expect(root).ToBeVisibleAsync();
        await root.Locator(".mud-treeview-item-arrow").ClickAsync();

        var emptyIcon = IconOf(tree, emptyName);
        var docsOnlyIcon = IconOf(tree, docsOnlyName);
        await Expect(emptyIcon).ToBeVisibleAsync(new() { Timeout = 15000 });

        Assert.Equal(Pastel, await ColorOfAsync(emptyIcon));
        Assert.NotEqual(Pastel, await ColorOfAsync(docsOnlyIcon));

        // Nothing leaks to the ancestor: the repository root holds these folders, so it is not empty.
        Assert.NotEqual(Pastel, await ColorOfAsync(root.Locator(".mud-treeview-item-icon > .mud-icon-root")));

        // Filing a document into the empty folder makes it non-empty on the next tree load.
        await AddDocumentAsync(http, emptyId, $"now-{suffix}");
        await page.ReloadAsync();
        await Expect(root).ToBeVisibleAsync(new() { Timeout = 15000 });
        await tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First
            .Locator(".mud-treeview-item-arrow").ClickAsync();
        await Expect(IconOf(tree, emptyName)).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.NotEqual(Pastel, await ColorOfAsync(IconOf(tree, emptyName)));
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
