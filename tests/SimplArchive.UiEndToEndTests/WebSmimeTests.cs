using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the self-service S/MIME certificate (#1332, ADR 0816): the account-menu dialog walks the
// state machine — generate flips to the set state with both download offers, both entrances disable, delete
// re-enables them. The crypto itself is proven where a mail client can open it (the E2E full-circle test);
// this covers the dialog the desktop's SmimeDialog is canonical for (ADR 0511). The test restores the
// deleted state at the end — the shared demo user must leave the suite as it found it.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebSmimeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSmimeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Smime_dialog_generates_disables_both_entrances_and_delete_re_enables()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Encrypted mail (S/MIME)…").ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Fresh state: no certificate, generate gated on the typed password.
        await Expect(dialog.GetByText("No certificate is set", new() { Exact = false })).ToBeVisibleAsync();
        var generate = dialog.GetByRole(AriaRole.Button, new() { Name = "Generate identity" });
        await Expect(generate).ToBeDisabledAsync();
        await dialog.GetByLabel("Password for the .p12 file").FillAsync("web-ui-secret");
        await Expect(generate).ToBeEnabledAsync();

        // Generate: the set state shows the artifacts once and disables both entrances.
        await generate.ClickAsync();
        await Expect(dialog.GetByText("Download both files now", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Download .p12" })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Download .mobileconfig" })).ToBeVisibleAsync();
        await Expect(generate).ToBeDisabledAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Upload certificate", Exact = false })).ToBeDisabledAsync();

        // Delete: both entrances come back.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete certificate" }).ClickAsync();
        await Expect(dialog.GetByText("No certificate is set", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Upload certificate", Exact = false })).ToBeEnabledAsync();
    }
}
