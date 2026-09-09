using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Deep links end to end (#761): Copy link puts the /go/{id} web URL on the clipboard, and opening that URL
// SIGNED OUT survives the OIDC round-trip — credential form, then landing navigated: containing folder open,
// the document selected, the detail pane describing it. The signed-out half is the one that rots silently
// (the issue's own words), so it is the one this test drives in full.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebDeepLinkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDeepLinkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_copied_link_lands_a_signed_out_recipient_on_the_selected_document()
    {
        // Sender: copy the link from the row's context menu (addressed from the row, ADR 0559).
        var page = await Ui.LoginAsync(_app, permissions: ["clipboard-read", "clipboard-write"]);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First.DblClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" }).First.DblClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2026-014" }).First;
        await row.Locator("button").Last.ClickAsync(); // the row's ⋮ menu (no native context menu on the web rows)
        await page.GetByText("Copy link", new() { Exact = true }).ClickAsync();
        var link = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.Contains("/go/", link);

        // Recipient: a fresh context — no session, no cache. The link must carry them through the credential
        // form and land them NAVIGATED, not merely signed in.
        var recipientContext = await _app.Browser.NewContextAsync(new() { AcceptDownloads = true });
        try
        {
            var recipient = await recipientContext.NewPageAsync();
            recipient.SetDefaultTimeout(60000);
            await recipient.GotoAsync(link);

            // The lander forwards straight into the OIDC round-trip: the server's credential form appears.
            await recipient.WaitForSelectorAsync("input[name='Email'], input[type='email']");
            await recipient.FillAsync("input[name='Email'], input[type='email']", SelfHostedAppFixture.AdminEmail);
            await recipient.FillAsync("input[name='Password'], input[type='password']", SelfHostedAppFixture.AdminPassword);
            await recipient.ClickAsync("button[type='submit'], input[type='submit']");

            // …and lands on the workbench with the document selected and described.
            var head = recipient.Locator(".wb-detail-head");
            await Expect(head).ToContainTextAsync("Offer 2026-014", new() { Timeout = 60000 });
            var selected = recipient.Locator("[data-pane='list'] .wb-list-row-selected");
            await Expect(selected).ToContainTextAsync("Offer 2026-014");
        }
        finally
        {
            await recipientContext.CloseAsync();
        }
    }

    // A deep link names the DOCUMENT, so filing it somewhere else must not strand the recipient. The link
    // itself is only an id and cannot rot — but landing is not one lookup: the workbench REVEALS the target,
    // opening the containing folder and expanding the tree to it, and that walk reads the ancestry at open
    // time. So the id survives a move trivially and the reveal is the half that could quietly follow the old
    // location, which is why the assertion is not "the document is named" but "the folder now around it is
    // the one that opened".
    //
    // Throwaway folders rather than the seeded document on purpose: the fixture is shared across the leg, so
    // moving demo content would relocate it under the sibling test that expects it in Contracts/Acme Corp.
    [Fact]
    public async Task A_link_still_lands_after_the_document_is_moved_to_another_folder()
    {
        var page = await Ui.LoginAsync(_app, permissions: ["clipboard-read", "clipboard-write"]);
        var box = "box-" + Guid.NewGuid().ToString("N")[..8];
        var item = "item-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        var nextFolderName = string.Empty;
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };

        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        await page.GetByText("Demo Repository").First.ClickAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(item);

        // The link is copied while the item is still INSIDE box — the address a colleague would now hold.
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = item }).First;
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Copy link", new() { Exact = true }).ClickAsync();
        var link = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.Contains("/go/", link);

        // Move it out to the repository root: same document, different parent, shorter ancestry.
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Move to").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).ClickAsync();
        await Expect(list.GetByText(item)).Not.ToBeVisibleAsync();   // gone from box

        // Follow the link that was copied BEFORE the move — a full reload, so the reveal runs from scratch.
        await page.GotoAsync(link);
        await Expect(page.Locator(".wb-detail-head")).ToContainTextAsync(item, new() { Timeout = 60000 });
        await Expect(list.Locator(".wb-list-row-selected")).ToContainTextAsync(item);

        // …and the folder that opened is the item's NEW one. `box` is visible only as a sibling row, which it
        // can only be if the root is what was opened — landing in the stale folder would show the item's old
        // neighbours instead, or nothing at all.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = box })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_link_to_something_unreachable_says_so_instead_of_a_blank_workbench()
    {
        var page = await Ui.LoginAsync(_app);
        await page.GotoAsync($"{_app.BaseUrl}/go/{Guid.NewGuid()}");
        await Expect(page.Locator(".mud-alert")).ToBeVisibleAsync();
        await page.GetByText("Back to the workbench").ClickAsync();
        await page.Locator(".wb-appbar").WaitForAsync();
    }
}
