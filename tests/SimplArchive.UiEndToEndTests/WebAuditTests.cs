using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs "Audit trail (first slice)" / "Audit trail hash chain"): the demo admin — provisioned with
// the dedicated CanViewAuditLog right — sees the Audit tab, which lists security-sensitive events newest-first.
// The interactive login itself is a recorded event (Auth.LoggedIn); the action filter narrows to it, and
// "Verify integrity" confirms the tenant's tamper-evidence hash chain is intact.
[Collection(UiCollection.Name)]
public class WebAuditTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAuditTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Audit_tab_shows_the_login_event_and_the_action_filter_narrows_it()
    {
        var page = await Ui.LoginAsync(_app);

        // The tab is gated on whoami.canViewAuditLog — visible for the demo admin.
        await page.Locator(".wb-tab[aria-label=\"Audit\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-audit")).ToBeVisibleAsync();

        // The interactive login recorded an Auth.LoggedIn event — it shows in the newest-first table.
        var table = page.Locator(".wb-audit-table");
        await Expect(table).ToBeVisibleAsync();
        await Expect(table.GetByText("Auth.LoggedIn").First).ToBeVisibleAsync();

        // The action filter narrows to exactly that action.
        await page.Locator(".wb-audit-filters input").First.FillAsync("Auth.LoggedIn");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync();
        await Expect(table.GetByText("Auth.LoggedIn").First).ToBeVisibleAsync();

        // Verify integrity → the tenant's hash chain is intact.
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify integrity" }).ClickAsync();
        // The verify chips ride in the TOOLBAR beside the buttons that produced them (#530 tranche 9).
        await Expect(page.Locator(".wb-audit .wb-search-bar").GetByText("Chain intact")).ToBeVisibleAsync();

        // Verify WORM → the sealed segments verify against the DB (ADR "Audit WORM segment verify").
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify WORM" }).ClickAsync();
        await Expect(page.Locator(".wb-audit .wb-search-bar").GetByText("WORM sealed intact")).ToBeVisibleAsync();

        // Export → the browser downloads an .ndjson file (ADR "Audit trail export").
        var download = await page.RunAndWaitForDownloadAsync(async () =>
            await page.GetByRole(AriaRole.Button, new() { Name = "Export" }).ClickAsync());
        Assert.EndsWith(".ndjson", download.SuggestedFilename);

        // The tenant admin sees the retention bar (ADR "Audit trail retention and purge") and can purge — the
        // demo events are fresh, so nothing is old enough and the purge reports 0 (non-destructive).
        var retentionBar = page.Locator(".wb-audit-retention");
        await Expect(retentionBar.GetByText("Retention:")).ToBeVisibleAsync();
        // Purge moved from the retention bar to the toolbar (#530 tranche 9); same accessible name.
        await page.GetByRole(AriaRole.Button, new() { Name = "Purge now" }).ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Purge", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Purged 0 event(s).")).ToBeVisibleAsync();
    }
}
