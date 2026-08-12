using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "User profile photo"): the corner shows the DisplayName (not the email); in the Users &
// groups tab, picking + cropping a photo for a user uploads a 256×256 PNG (browser canvas → PUT) and the
// user's avatar image appears.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebProfilePhotoTests
{
    // A 1×1 PNG — the crop canvas rescales it to a valid 256×256 PNG on upload.
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private readonly SelfHostedAppFixture _app;

    public WebProfilePhotoTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Corner_shows_display_name_and_a_photo_can_be_set_for_a_user()
    {
        var page = await Ui.LoginAsync(_app);

        // The corner shows the DisplayName; the email is gone.
        var appbar = page.Locator(".wb-appbar");
        await Expect(appbar).ToContainTextAsync("Demo Admin");
        await Expect(appbar).Not.ToContainTextAsync("demo@simplarchive.local");

        // Users & groups tab → select the Demo Admin user.
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await page.Locator(".wb-ug-rows").GetByText("Demo Admin", new() { Exact = true }).ClickAsync();

        // Change photo… → the dialog; pick an image, then Save (default centered crop).
        await page.GetByRole(AriaRole.Button, new() { Name = "Change photo…" }).ClickAsync();
        await page.SetInputFilesAsync("input.pp-file", new FilePayload
        {
            Name = "avatar.png",
            MimeType = "image/png",
            Buffer = Convert.FromBase64String(OnePixelPngBase64),
        });

        var save = page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Save" });
        await Expect(save).ToBeEnabledAsync(); // enabled once the image has loaded into the cropper
        await save.ClickAsync();

        // The upload succeeds and the user's avatar image now shows in the photo section.
        await Expect(page.GetByText("Profile photo updated.")).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-ug-photo img")).ToBeVisibleAsync();
    }
}
