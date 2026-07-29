using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web inbox file-list drop-zone (ADR "Inbox file-list drop-zone"): dropping OS files onto the inbox list
// uploads them straight into the S3-backed inbox. Playwright can't perform a real OS file drag, so the drop is
// synthesized with a DataTransfer holding a File; the JS handler presigns + PUTs it exactly like a real drop.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebInboxDropTests
{
    private readonly SelfHostedAppFixture _app;

    public WebInboxDropTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dropping_a_file_on_the_inbox_list_uploads_it()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "webdrop-" + Guid.NewGuid().ToString("N")[..8];

        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        var zone = page.Locator(".wb-inbox-drop");
        await Expect(zone).ToBeVisibleAsync();
        await Expect(zone).ToContainTextAsync("Drop files here");

        // Build a DataTransfer carrying one file, then dispatch dragover + drop onto the zone (a real OS file
        // drag isn't possible in a headless browser).
        var dataTransfer = await page.EvaluateHandleAsync(
            @"n => { const dt = new DataTransfer();
                     dt.items.add(new File(['dropped via the web drop-zone'], n + '.txt', { type: 'text/plain' }));
                     return dt; }",
            name);
        await zone.DispatchEventAsync("dragover", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });
        await zone.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });

        // The dropped file uploads to the inbox and appears in the list. The upload is confirmed 200, but the
        // S3 LIST can lag the just-written object by a moment, so re-list via Refresh until it shows.
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        for (var i = 0; i < 12 && await row.CountAsync() == 0; i++)
        {
            await page.WaitForTimeoutAsync(1000);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh" }).ClickAsync();
        }

        await Expect(row).ToBeVisibleAsync();
    }
}
