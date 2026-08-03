using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0291): an un-classified inbox item shows in square brackets; staging a mask writes the
// {name}.mask.json sidecar and flips the item to un-bracketed.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebInboxMaskTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxMaskTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Staging_a_mask_flips_the_unclassified_indicator()
    {
        var page = await Ui.LoginAsync(_app);
        var file = "maskme-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        // Upload to the inbox → the un-classified item shows in brackets.
        await page.Locator(".wb-tab[aria-label=\"Inbox\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#inbox-file-input", new FilePayload { Name = file, MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("mask me") });
        await Expect(page.GetByText("[" + file + "]")).ToBeVisibleAsync();

        // Select it → the staging pane; assign the Basic Entry mask and save.
        await page.Locator(".wb-list-row").Filter(new() { HasText = file }).First.ClickAsync();
        await page.Locator(".wb-mask-edit .mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = "Basic Entry" }).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).First.ClickAsync();

        // The indicator flips: the list item is no longer bracketed, now the plain name.
        await Expect(page.GetByText("[" + file + "]")).Not.ToBeVisibleAsync();
        await Expect(page.Locator(".wb-search-results").GetByText(file, new() { Exact = true })).ToBeVisibleAsync();
    }
}
