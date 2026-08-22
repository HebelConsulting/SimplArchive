using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0532): the demo admin (a CanManageIntrays holder) uploads an item to their own intray, hands it
// to another user via the "Send to…" dialog — it leaves the admin's intray — and then, using the admin user-picker,
// opens that user's intray and sees the item there.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebIntraySendTests
{
    private readonly SelfHostedAppFixture _app;

    public WebIntraySendTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Send_an_own_item_to_another_user_then_triage_it_via_the_user_picker()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var recipient = "Send User " + suffix;

        // Create the recipient so the "Send to…" dialog and the admin user-picker have a target.
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await page.Locator(".wb-ug-toolbar").GetByRole(AriaRole.Button).First.ClickAsync(); // New menu
        await page.GetByText("New user").ClickAsync();
        var newUser = page.Locator(".mud-dialog");
        await newUser.Locator("input").Nth(0).FillAsync($"send-{suffix}@example.test");
        await newUser.Locator("input").Nth(1).FillAsync(recipient);
        await newUser.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(page.GetByText("User created.")).ToBeVisibleAsync();

        // Intray: upload a file into the admin's own intray.
        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        var name = "senditem" + suffix;
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("hand-off via the intray"),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // Row ⋮ menu → "Send to…" → pick the recipient user → Send.
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("Send to").First.ClickAsync();

        var send = page.Locator(".mud-dialog").Filter(new() { HasText = "Send to" });
        await send.Locator(".mud-input-control").First.ClickAsync();        // open the destination select
        await page.Locator(".mud-list-item").Filter(new() { HasText = recipient }).First.ClickAsync();
        await send.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();

        // It leaves the admin's own intray...
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // ...and the admin (CanManageIntrays) opens the recipient's intray via the user-picker and sees it there.
        // The picker lives in .wb-intray-filters, below the action bar and above the list it filters (#521) —
        // it used to sit in .wb-search-bar, where its height set every button's beside it.
        await page.Locator(".wb-intray-filters .mud-select .mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = recipient }).First.ClickAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
    }
}
