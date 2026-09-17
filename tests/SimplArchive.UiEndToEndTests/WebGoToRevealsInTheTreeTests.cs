using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// "Go to …" on a reference takes the TREE along, not just the list pane (#1264).
//
// The desktop half of this was a real defect: its Go to opened the contents pane without re-syncing the tree,
// while the sibling search-hit path revealed correctly — one act behaving two ways depending on where it
// started. The web is ASSERTED here rather than assumed: its SelectFolderAsync has a reveal ladder that Go to
// routes through, so it may well have been right all along. Either way the behaviour is now pinned, because
// "the tree followed" is invisible in a screenshot and silently lost in a refactor.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebGoToRevealsInTheTreeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebGoToRevealsInTheTreeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Go_to_on_a_reference_marks_the_targets_real_parent_in_the_tree()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var homeName = $"gtwhome{suffix}";
        var shortcutFolderName = $"gtwb{suffix}";
        var docName = $"gtwdoc{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repoId = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories")
            .EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        // The document lives NESTED — repo → branch A → home — while the shortcut sits in branch B. Nested on
        // purpose: a target at the repository root would already be a top-level tree node, so the test could
        // pass without any ancestor expansion happening at all.
        var branchA = await CreateFolderAsync(http, repoId, $"gtwa{suffix}");
        var branchB = await CreateFolderAsync(http, repoId, shortcutFolderName);
        var home = await CreateFolderAsync(http, branchA, homeName);

        (await http.PostAsJsonAsync($"/api/documents/{home}/children", new { name = docName })).EnsureSuccessStatusCode();
        var docId = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{home}/children"))
            .GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == docName)
            .GetProperty("id").GetGuid();

        (await http.PostAsJsonAsync($"/api/documents/{branchB}/references", new { targetId = docId })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // Stand in branch B, where the shortcut lives — what a user does before clicking Go to.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = shortcutFolderName }).First)
            .ToBeVisibleAsync(new() { Timeout = 20000 });
        await list.Locator(".wb-list-row").Filter(new() { HasText = shortcutFolderName }).First.DblClickAsync();

        var shortcutRow = list.Locator(".wb-list-row").Filter(new() { HasText = docName }).First;
        await Expect(shortcutRow).ToBeVisibleAsync(new() { Timeout = 20000 });

        // Go to — from the row's own menu, which is how a user reaches it.
        await shortcutRow.Locator("button").Last.ClickAsync();
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Go to" }).First.ClickAsync();

        // THE ASSERTION THE ISSUE IS ABOUT: the tree marks the target's real home folder. Asserting only that
        // the LIST moved is what a test written from the symptom would do — and on the desktop that assertion
        // passed throughout, while the tree sat in the wrong branch.
        await Expect(tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = homeName }).First)
            .ToBeVisibleAsync(new() { Timeout = 20000 });

        // And the list went too, showing the target in its real home.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = docName }).First)
            .ToBeVisibleAsync(new() { Timeout = 20000 });
    }

    private static async Task<Guid> CreateFolderAsync(HttpClient http, Guid parentId, string name)
    {
        (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name })).EnsureSuccessStatusCode();
        return (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{parentId}/children"))
            .GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();
    }
}
