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
    public async Task The_ribbon_button_opens_webdav_and_says_how_to_mount_it()
    {
        var page = await Ui.LoginAsync(_app);

        // The ribbon is the discoverable entry point (#461) — the account menu keeps working, but a user looking
        // for their documents looks where the document actions are.
        await page.Locator("[data-tour='action-webdav']").ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Every value the user must paste into an OS dialog has its own copy button. The username's was missing
        // while the URL and password had one — the shape of gap nobody notices until they need it. Counted
        // rather than located per field: MudBlazor renders an adornment as an icon button inside the input, and
        // pinning that internal structure would make this test fail on a MudBlazor upgrade rather than on a
        // regression.
        await Expect(dialog.GetByRole(AriaRole.Textbox, new() { Name = "Mount URL (all repositories)" })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Textbox, new() { Name = "Username" })).ToBeVisibleAsync();
        Assert.Equal(2, await dialog.Locator(".mud-input-adornment button").CountAsync());

        // A browser cannot mount, so the substitute is telling the visitor how (ADR 0560). Asserted as the
        // HEADING plus non-empty guidance rather than a specific OS's wording: the instructions are chosen from
        // the visitor's platform, and this suite runs on macOS locally and Linux in CI.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();
        await Expect(dialog.GetByText("Copy this password now")).ToBeVisibleAsync();

        await Expect(dialog.GetByText("How to mount it")).ToBeVisibleAsync();

        // The generated password is the third copyable value.
        Assert.Equal(3, await dialog.Locator(".mud-input-adornment button").CountAsync());
        var steps = await dialog.Locator(".mud-typography-body2").AllTextContentsAsync();
        Assert.Contains(steps, s => s.Contains("Connect to Server", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Map network drive", StringComparison.OrdinalIgnoreCase)
            || s.Contains("davs://", StringComparison.OrdinalIgnoreCase)
            || s.Contains("WebDAV client", StringComparison.OrdinalIgnoreCase));
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

        // Inbox tab → Copy WebDAV URL → the address of THIS TAB'S folder inside the single "SimplArchive"
        // mount (ADR 0509). Deep-linked rather than the bare mount root: the desktop's button opens the tab's
        // own folder, and handing over its address is the nearest thing a browser is allowed to do (ADR 0560).
        await page.Locator(".wb-tab[aria-label=\"Inbox\"]").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy WebDAV URL" }).ClickAsync();
        await Expect(page.GetByText("Copied WebDAV URL")).ToBeVisibleAsync();
        Assert.EndsWith("/SimplArchive/Personal/Inbox", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }

    // The two tabs must copy DIFFERENT addresses — that is the whole point of the deep link, and a shared
    // handler that ignored its argument would pass the Inbox test above while silently doing nothing here.
    [Fact]
    public async Task The_checkout_tab_copies_its_own_folder_not_the_inbox_one()
    {
        var page = await Ui.LoginAsync(_app, new[] { "clipboard-read", "clipboard-write" });

        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("WebDAV access…").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();
        await Expect(dialog.GetByText("Copy this password now")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy WebDAV URL" }).ClickAsync();
        await Expect(page.GetByText("Copied WebDAV URL")).ToBeVisibleAsync();
        Assert.EndsWith("/SimplArchive/Personal/Check-out", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }
}
