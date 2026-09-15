using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Revoking an external link from the web client — the WRITE, not the button.
//
// It exists because #1218 moved the If-Match header out of four razor dialogs and into a client, and nothing
// exercised the paths it moved. WebExternalLinkRowActionsTests asserts the three row actions are VISIBLE and
// that Revoke fits inside the dialog; it never clicks either one. So the revoke and renew requests — the two
// operations carrying a precondition — could have lost their header without a single test noticing.
//
// The failure would at least be loud rather than silent: both routes call RequireIfMatch, so a dropped header
// is 428 and a stale one 412. But "loud at runtime" is not "caught before release", and the whole premise of
// #1175's client half is that FORGETTING is what happens. A test that watches a button appear cannot see a
// request that was never sent correctly.
//
// THE DOCUMENT IS CHOSEN, not convenient. "Maintenance agreement WV-2026-118" is referenced by no other UI
// test and carries no seeded link, so this dialog opens empty and "exactly one row" means the one this test
// made. The obvious choice — the same "service agreement" the create test uses — is wrong twice over: the demo
// seeder files an external link against it, and WebExternalLinkCreateTests adds another and leaves it. Any
// assertion counting rows there passes alone and fails in a full run, which is the shared-mutable-data shape
// that cost most of a session in #420.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebExternalLinkRevokeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebExternalLinkRevokeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Revoking_a_link_sends_its_ETag_and_the_row_disappears()
    {
        var page = await Ui.LoginAsync(_app);

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var folder in new[] { "Contracts", "Acme Corp" })
        {
            var folderRow = page.Locator(".wb-list-row").Filter(new() { HasText = folder });
            await folderRow.First.WaitForAsync(new() { Timeout = 15000 });
            await folderRow.First.DblClickAsync();
        }

        var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "Maintenance agreement" });
        await doc.First.WaitForAsync(new() { Timeout = 15000 });
        await doc.First.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "External links…" }).First.ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await dialog.WaitForAsync(new() { Timeout = 10000 });

        var create = dialog.GetByRole(AriaRole.Button, new() { Name = "Create external link…" });
        await Expect(create).ToBeVisibleAsync(new() { Timeout = 15000 });
        await create.ClickAsync();

        // Revealed exactly once, which is also how we know the create landed rather than failing quietly.
        await Expect(dialog.GetByText("shown only once")).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Exact = true because Playwright matches an accessible name by SUBSTRING, so a bare "Revoke" would
        // also match any longer button name starting with it — a strict-mode violation that shows up as a
        // race rather than a constant failure.
        var revoke = dialog.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true });
        await Expect(revoke).ToHaveCountAsync(1, new() { Timeout = 15000 });
        await revoke.ClickAsync();

        // The listing filters RevokedAt == null, so a successful revoke removes the row — and with it the only
        // Revoke button. That is what makes this an assertion about the REQUEST: a revoke whose If-Match never
        // arrived answers 428, the dialog reports an error, and the row stays exactly where it was.
        await Expect(revoke).ToHaveCountAsync(0, new() { Timeout = 15000 });
    }
}
