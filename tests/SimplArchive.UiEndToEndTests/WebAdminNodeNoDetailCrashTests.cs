using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Clearing the list selection while a SYNTHETIC Administration node is selected must not take the workbench down.
//
// FOUND IN LIVE TESTING, and it is a crash rather than a wrong value:
//
//   Unhandled exception rendering component: 404, Not Found
//     at SimplArchive.Client.Services.BrowseService.FetchRelAsync(Guid documentId, String rel)
//     at SimplArchive.Client.Pages.Home.LoadFolderSubscriptionAsync(BrowseNode folder)
//     at SimplArchive.Client.Pages.Home.ShowFolderDetailAsync(BrowseNode folder)
//     at SimplArchive.Client.Pages.Home.ClearListSelectionAsync()
//
// The Administration branch and the Personal launchers stand for NO document — they carry Guid.Empty — so
// describing one fetches `/api/documents/00000000-…`, which 404s. Thrown from an async event handler, that
// reaches Blazor's error banner and the whole page is finished.
//
// REACHABLE ONLY SINCE #1160 gave those nodes a real selection. Before, selecting one early-returned and
// `_selectedFolder` still pointed at the last real folder — so this path existed and was never entered. A
// feature made an old latent fault reachable, which is the kind of thing only using the app finds.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAdminNodeNoDetailCrashTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAdminNodeNoDetailCrashTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Escape_while_an_Administration_node_is_selected_does_not_kill_the_page()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // The user's own evidence was a LOG LINE, so that is what this listens for. A UI proxy (the error
        // banner) proved too weak: written against it, this test passed on the UNFIXED build, which would have
        // shipped a guard nobody had watched fail.
        var failures = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error" || message.Text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(message.Text);
            }
        };
        page.PageError += (_, error) => failures.Add(error);

        var administration = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Administration" }).First;
        await Expect(administration).ToBeVisibleAsync();
        await administration.ClickAsync();

        // Its contents arrive (#1160), which is what proves the node really is the current selection.
        await Expect(list.GetByText("Users", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });

        // The trigger: Esc, or a click on the list's EMPTY area — "nothing is selected any more, fall back to
        // describing the open folder" (ADR 0703). The open "folder" here has no document behind it.
        //
        // PressAsync on the pane, not page.Keyboard: the handler is `@onkeydown` on the pane root (tabindex=0),
        // so the key only arrives if the pane has focus. A page-level press reached nothing, and the test then
        // PASSED against the unfixed build — a green run that proved only that the trigger had missed.
        await list.PressAsync("Escape");

        await Task.Delay(1500);   // let any async handler finish throwing

        // The assertion that matches the report: nothing was logged as an unhandled render failure.
        Assert.True(
            failures.All(f => !f.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)
                && !f.Contains("Not Found", StringComparison.OrdinalIgnoreCase)),
            "the page reported an unhandled exception — a synthetic node has no document to describe, so "
            + "fetching one 404s and the throw escapes the async handler:\n  " + string.Join("\n  ", failures));

        // And the banner, which is what the user sees when the component tree dies.
        await Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync(new() { Timeout = 5000 });

        // And the workbench is still usable: the tree still answers, which it would not if the render had died.
        await Expect(administration).ToBeVisibleAsync();
        await Expect(list.GetByText("Users", new() { Exact = true })).ToBeVisibleAsync();
    }
}
