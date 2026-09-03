using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs "Tenant-admin settings tab" + "Per-group tenant settings", #530 tranche 10): the demo admin
// sees the Tenant tab — reference card first, then the settings in groups, each read-only with its own pencil;
// a group's pencil makes ONLY that group editable (Save/Cancel in its header row, the other pencils hidden).
// Kept read-only-ish (no destructive change committed here) so the shared demo tenant stays clean.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTenantSettingsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTenantSettingsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tenant_tab_shows_reference_first_groups_and_per_group_edit()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-tenant")).ToBeVisibleAsync();

        // The toolbar carries the three launchers (#530 tranche 10 — Convert scans moved here from the
        // Repositories ribbon); the reference card is the FIRST thing in the body.
        var wrap = page.Locator(".wb-tenant-wrap");
        await Expect(wrap.GetByRole(AriaRole.Button, new() { Name = "New repository" })).ToBeVisibleAsync();
        await Expect(wrap.GetByRole(AriaRole.Button, new() { Name = "Convert scans" })).ToBeVisibleAsync();

        var view = page.Locator(".wb-tenant");
        await Expect(view.GetByText("Reference").First).ToBeVisibleAsync();
        await Expect(view.GetByText("Tenant ID")).ToBeVisibleAsync();

        // The nine decided groups render, in order (Mail joined with #793).
        foreach (var group in new[] { "General", "Documents & capture", "Security & sign-in", "Records & compliance", "Check-out", "Storage", "Mail", "External links", "Audit streaming (SIEM)" })
        {
            await Expect(view.Locator(".wb-tenant-group-head").Filter(new() { HasText = group })).ToBeVisibleAsync();
        }

        // Each explainable setting carries an info button (hover tooltip) — eighteen: the seventeen from the
        // regrouping plus the Mail group's IMAP seed default (#793). The webhook secret + delivery-health
        // buttons render only in edit mode / when a webhook is configured, and the count still DEPENDS on
        // external links being on for the demo tenant (ADR 0214).
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Explanation" })).ToHaveCountAsync(18);

        // The storage-usage line (ADR "Per-tenant storage quota") shows how much is used vs the limit.
        await Expect(view.GetByText("Used:")).ToBeVisibleAsync();

        // Read-only everywhere: nine pencils, no Save/Cancel.
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToHaveCountAsync(9);
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeHiddenAsync();

        // A group's pencil → Save/Cancel appear IN ITS HEADER ROW, and the other pencils hide (starting a
        // second edit would silently discard the first).
        var general = view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "General" }).First;
        await general.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(general.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeVisibleAsync();
        await Expect(general.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeVisibleAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToHaveCountAsync(0);

        // Cancel discards without persisting anything and returns every pencil.
        await general.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToHaveCountAsync(9);
    }

    // Per-group editability: a group's pencil enables ITS fields and nobody else's — the point of the split.
    [Fact]
    public async Task Modules_section_renders_with_the_empty_state()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
        var view = page.Locator(".wb-tenant");

        // The Modules section (ADRs 0740/0743) renders after the settings groups — a plain header without a
        // pencil (rows, not fields), and with no module assemblies staged the empty state says so rather
        // than showing nothing (an absent section would read as "feature missing", not "nothing installed").
        await Expect(view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "Modules" })).ToBeVisibleAsync();
        await Expect(view.GetByText("No modules are installed on this server.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Fields_are_disabled_until_their_groups_edit()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
        var view = page.Locator(".wb-tenant");
        await Expect(view).ToBeVisibleAsync();

        var nameInput = view.Locator(".wb-setting-row").First.Locator("input");
        var mfaSwitch = view.Locator(".wb-setting-row").Filter(new() { HasText = "Require two-factor authentication" }).Locator("input");
        await Expect(nameInput).ToBeDisabledAsync();
        await Expect(mfaSwitch).ToBeDisabledAsync();

        // General's pencil enables the name — and NOT the security switch.
        var general = view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "General" }).First;
        await general.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(nameInput).ToBeEnabledAsync();
        await Expect(mfaSwitch).ToBeDisabledAsync();
        await general.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // Security's pencil enables the switch — and NOT the name.
        var security = view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "Security & sign-in" }).First;
        await security.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(mfaSwitch).ToBeEnabledAsync();
        await Expect(nameInput).ToBeDisabledAsync();

        // Cancel returns everything to disabled (and persists nothing).
        await security.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(nameInput).ToBeDisabledAsync();
        await Expect(mfaSwitch).ToBeDisabledAsync();
    }

    // ADR "Sensitivity clearance enforcement": the Enforce-clearance switch lives in the Security group,
    // disabled until that group edits. Left OFF (Cancel) so the shared demo tenant stays unenforced.
    [Fact]
    public async Task Enforce_clearance_switch_renders_and_is_disabled_until_its_groups_edit()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
        var view = page.Locator(".wb-tenant");
        await Expect(view).ToBeVisibleAsync();

        var clearanceSwitch = view.Locator(".wb-setting-row").Filter(new() { HasText = "Enforce sensitivity clearance" }).Locator("input");
        await Expect(clearanceSwitch).ToBeDisabledAsync();

        var security = view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "Security & sign-in" }).First;
        await security.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(clearanceSwitch).ToBeEnabledAsync();

        await security.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(clearanceSwitch).ToBeDisabledAsync();
    }
}
