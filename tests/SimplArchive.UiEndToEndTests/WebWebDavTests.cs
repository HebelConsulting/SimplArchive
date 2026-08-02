using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the WebDAV gateway (ADR "WebDAV gateway"): the account-menu "WebDAV access…" dialog shows the
// mount URL and generates an app-specific password (shown once).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebWebDavTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWebDavTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task WebDav_dialog_shows_the_mount_url_and_generates_a_password()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("WebDAV access…").ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByText("Mount the archive as a network drive")).ToBeVisibleAsync();

        // The dialog exposes the single "SimplArchive" mount-URL field (the whole tree lives under it, ADR 0509).
        await Expect(dialog.GetByRole(AriaRole.Textbox, new() { Name = "Mount URL (all repositories)" })).ToBeVisibleAsync();

        // Generate a password → it's shown once (the "shown once" alert appears).
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();
        await Expect(dialog.GetByText("Copy this password now")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Inbox_tab_copies_the_personal_webdav_url()
    {
        var page = await Ui.LoginAsync(_app, new[] { "clipboard-read", "clipboard-write" });

        // Ensure WebDAV is enabled (generate a password via the account dialog), then close it.
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("WebDAV access…").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();
        await Expect(dialog.GetByText("Copy this password now")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

        // Inbox tab → Copy WebDAV URL → the single "SimplArchive" mount URL lands on the clipboard (ADR 0509).
        await page.Locator(".wb-tab").Filter(new() { HasText = "Inbox" }).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy WebDAV URL" }).ClickAsync();
        await Expect(page.GetByText("Copied WebDAV URL")).ToBeVisibleAsync();
        Assert.EndsWith("/SimplArchive", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }
}
