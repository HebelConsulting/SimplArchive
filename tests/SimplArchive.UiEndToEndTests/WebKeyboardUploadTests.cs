using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Intray's Upload is actuable by KEYBOARD (issue #511): it used to be a <label> over a display:none file
// input — no focus, no Enter/Space, no button role — so a keyboard-only user had no in-app path into the Inbox
// at all. Now it is a real button forwarding to the input, and this test drives the whole upload without a
// single mouse event on the control: focus it, press Enter, feed the chooser, see the item land in the list.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebKeyboardUploadTests
{
    private readonly SelfHostedAppFixture _app;

    public WebKeyboardUploadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Upload_button_is_focusable_and_Enter_opens_the_chooser()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "kbdupload" + Guid.NewGuid().ToString("N")[..8];

        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();

        // The role locator finding it AT ALL is half the assertion — a label has no button role, and this
        // exact locator matched nothing before the fix (which is how #511 was found).
        var upload = page.GetByRole(AriaRole.Button, new() { Name = "Upload to intray", Exact = true });
        await Expect(upload).ToBeVisibleAsync();

        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await upload.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
        });

        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("uploaded without a mouse"),
        });

        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
    }
}
