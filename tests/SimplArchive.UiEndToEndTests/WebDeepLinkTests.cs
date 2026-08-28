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
