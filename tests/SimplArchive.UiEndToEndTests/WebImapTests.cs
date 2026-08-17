using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the IMAP endpoint's account surface (ADR 0594, #562): the account-menu "Email access
// (IMAP)…" dialog shows the server values, generates the app-specific password (shown once), and carries the
// show-every-document switch. The protocol itself is covered where a mail client can reach it (the MailKit
// E2E suite); this covers the dialog the desktop's ImapDialog is canonical for (ADR 0511).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebImapTests
{
    private readonly SelfHostedAppFixture _app;

    public WebImapTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Imap_dialog_shows_the_server_values_and_generates_a_password()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Email access (IMAP)…").ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Textbox, new() { Name = "Server" })).ToBeVisibleAsync();

        // Generate a password → shown once, with the same copy-now alert the WebDAV dialog trained users on.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();
        await Expect(dialog.GetByText("Copy this password now")).ToBeVisibleAsync();

        // The self-service view switch is present and enabled (the fixture's endpoint is on).
        var toggle = dialog.Locator(".mud-switch input[type=checkbox]");
        await Expect(toggle).ToBeEnabledAsync();
    }
}
