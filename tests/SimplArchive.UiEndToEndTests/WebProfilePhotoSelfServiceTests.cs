using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0310): the self-service path — set your own photo from the corner avatar (not the admin
// tab) so the corner shows it, then Remove it via the Users & groups tab so it reverts to initials.
[Collection(UiCollection.Name)]
public class WebProfilePhotoSelfServiceTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private readonly SelfHostedAppFixture _app;

    public WebProfilePhotoSelfServiceTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_own_photo_from_the_corner_then_remove_it()
    {
        var page = await Ui.LoginAsync(_app);

        // Self-service: click the corner avatar → account menu → Change photo → the photo dialog.
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Change photo…").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await page.SetInputFilesAsync("#pp-file", new FilePayload
        {
            Name = "me.png",
            MimeType = "image/png",
            Buffer = Convert.FromBase64String(OnePixelPngBase64),
        });
        var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save" });
        await Expect(save).ToBeEnabledAsync();
        await save.ClickAsync();

        // The corner now shows the photo (an <img>, not initials).
        await Expect(page.GetByText("Profile photo updated.")).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-appbar img")).ToBeVisibleAsync();

        // Remove it from the Users & groups tab → the avatar reverts to initials (no <img>).
        await page.Locator(".wb-tab").Filter(new() { HasText = "Users & groups" }).First.ClickAsync();
        await page.Locator(".wb-ug-rows").GetByText("Demo Admin", new() { Exact = true }).ClickAsync();
        await Expect(page.Locator(".wb-ug-photo img")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Expect(page.GetByText("Photo removed.")).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-ug-photo img")).Not.ToBeVisibleAsync();
    }
}
