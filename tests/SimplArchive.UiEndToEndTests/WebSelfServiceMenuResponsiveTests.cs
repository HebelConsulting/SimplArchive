using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Responsive fix: the self-service (account) avatar is the rightmost app-bar item, so on a narrow tablet/phone
// the non-wrapping header used to overflow and push it off-screen. It must stay within the viewport and remain
// clickable at every size. Playwright's IsVisible can't see an off-screen-overflow element, so we assert the
// avatar's bounding box stays inside the viewport, then that clicking it opens the account menu.
[Collection(UiCollection.Name)]
public class WebSelfServiceMenuResponsiveTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSelfServiceMenuResponsiveTests(SelfHostedAppFixture app) => _app = app;

    [Theory]
    [InlineData(1024, 768)]  // tablet (landscape iPad-ish)
    [InlineData(768, 1024)]  // tablet (portrait)
    [InlineData(390, 844)]   // phone
    [InlineData(360, 740)]   // small phone
    public async Task Self_service_avatar_stays_on_screen_and_opens(int width, int height)
    {
        // LoginAsync runs at the default 1280px width (where the display name still shows); shrink afterwards.
        var page = await Ui.LoginAsync(_app);
        await page.SetViewportSizeAsync(width, height);

        var avatar = page.Locator(".wb-userbox");
        await Expect(avatar).ToBeVisibleAsync();

        // The avatar must be fully within the viewport horizontally — not pushed past the right edge (the bug).
        var box = await avatar.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.X >= 0, $"avatar starts off the left edge at {width}px: x={box.X}");
        Assert.True(box.X + box.Width <= width + 1, $"avatar overflows the right edge at {width}px: x={box.X}, w={box.Width}, viewport={width}");

        // …and it still opens the self-service menu.
        await avatar.ClickAsync();
        await Expect(page.GetByText("Log out")).ToBeVisibleAsync();
    }
}
