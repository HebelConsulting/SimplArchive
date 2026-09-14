using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The tree follows you onto a deep branch it had not loaded (#1150). A deep link — or a Go to, or a list drill
// onto an unopened branch — can land the workbench on a folder several levels below any open tree node.
// TreeState.RevealAsync searched only the ALREADY-LOADED tree and gave up on a miss, scheduling nothing to try
// again: so the tree marked nothing, silently, and the user stood in one folder while the tree pointed at
// another. The fix escalates a miss to loading the ancestor chain (one /ancestors call) and expanding down.
//
// This reproduces DETERMINISTICALLY where the original bug did not (it needed a long single-process run to load
// the app into the right state, which is why CI's four short legs never saw it): a FULL reload rebuilds the tree
// to its roots, so revealing a folder two levels down ALWAYS needs the chain loaded — no race, no timing.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTreeRevealDeepBranchTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTreeRevealDeepBranchTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_deep_link_marks_its_containing_folder_in_the_tree()
    {
        var page = await Ui.LoginAsync(_app, permissions: ["clipboard-read", "clipboard-write"]);
        var list = page.Locator("[data-pane='list']");

        // Reach a deeply-filed document and copy its link: Demo Repository → Contracts → Acme Corp → Offer.
        // Acme Corp is two levels below the repository root, which is what makes the reveal need the chain.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First.DblClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" }).First.DblClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2026-014" }).First;
        await row.Locator("button").Last.ClickAsync(); // the row's ⋮ menu
        await page.GetByText("Copy link", new() { Exact = true }).ClickAsync();
        var link = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.Contains("/go/", link);

        // A FULL reload: the tree is back to its roots, so the containing folder is NOT in it — the exact state
        // where the old reveal gave up and marked nothing.
        await page.GotoAsync(link);

        // Landed: the document is selected and described.
        await Expect(page.Locator(".wb-detail-head")).ToContainTextAsync("Offer 2026-014", new() { Timeout = 60000 });

        // And the tree FOLLOWED: the containing folder is present and marked current. On a roots-only tree this
        // can only be true if the ancestor chain was loaded and expanded down to it — the fix for #1150. Before
        // it, this node was absent from the tree entirely and the assertion times out.
        var current = page.Locator("[data-pane='tree'] .wb-tree-current");
        await Expect(current).ToContainTextAsync("Acme Corp");
    }
}
