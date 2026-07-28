using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR "Tenant-admin settings tab"): the demo admin (a tenant admin) sees the Tenant tab, which shows
// the tenant settings read-only; clicking Edit makes the fields editable and Save persists. Kept read-only-ish
// (no destructive change committed here) so the shared demo tenant stays clean for the rest of the suite.
[Collection(UiCollection.Name)]
public class WebTenantSettingsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTenantSettingsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tenant_tab_shows_settings_and_edit_toggles_editability()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab").Filter(new() { HasText = "Tenant" }).First.ClickAsync();
        await Expect(page.Locator(".wb-tenant")).ToBeVisibleAsync();

        var view = page.Locator(".wb-tenant");
        await Expect(view.GetByText("Tenant settings")).ToBeVisibleAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "New repository" })).ToBeVisibleAsync();

        // Each explainable setting carries an info button (hover tooltip). Name, OCR, audit retention,
        // check-out auto-release, check-out expiry warning, WORM lock mode, storage quota, the storage Recompute
        // action, incomplete-upload cleanup, require-MFA, allow-passkey-login, require-disposition-review,
        // restrict-tags-to-catalog, enforce-clearance, and the audit webhook URL → fifteen (the webhook secret +
        // delivery-health buttons render only in edit mode / when a webhook is configured, so aren't counted
        // here). ADR "Sensitivity clearance enforcement" added the enforce-clearance one.
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Explanation" })).ToHaveCountAsync(15);

        // The storage-usage line (ADR "Per-tenant storage quota") shows how much is used vs the limit.
        await Expect(view.GetByText("Used:")).ToBeVisibleAsync();

        // Read-only until Edit: Save/Cancel are hidden, Edit is shown.
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeHiddenAsync();

        // Edit → Save + Cancel appear.
        await view.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeVisibleAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeVisibleAsync();

        // Cancel discards without persisting anything and returns to read-only.
        await view.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(view.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToBeVisibleAsync();
    }

    // The reported bug: fields must be genuinely non-editable until Edit (disabled/greyed), including the
    // switches (MudSwitch's ReadOnly didn't actually block toggling) — and become editable in edit mode.
    [Fact]
    public async Task Fields_are_disabled_until_edit()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab").Filter(new() { HasText = "Tenant" }).First.ClickAsync();
        var view = page.Locator(".wb-tenant");
        await Expect(view).ToBeVisibleAsync();

        // A representative text field (the tenant name, first setting row) and the previously-buggy switch
        // (require two-factor) are both disabled before Edit…
        var nameInput = view.Locator(".wb-setting-row").First.Locator("input");
        var mfaSwitch = view.Locator(".wb-setting-row").Filter(new() { HasText = "Require two-factor authentication" }).Locator("input");
        await Expect(nameInput).ToBeDisabledAsync();
        await Expect(mfaSwitch).ToBeDisabledAsync();

        // …and enabled after Edit.
        await view.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(nameInput).ToBeEnabledAsync();
        await Expect(mfaSwitch).ToBeEnabledAsync();

        // Cancel returns them to disabled (and persists nothing, keeping the shared demo tenant clean).
        await view.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(nameInput).ToBeDisabledAsync();
        await Expect(mfaSwitch).ToBeDisabledAsync();
    }

    // ADR "Sensitivity clearance enforcement": the Tenant tab exposes the Enforce-clearance switch, disabled
    // until Edit. Left OFF (Cancel) so the shared demo tenant isn't put into clearance-enforced mode.
    [Fact]
    public async Task Enforce_clearance_switch_renders_and_is_disabled_until_edit()
    {
        var page = await Ui.LoginAsync(_app);

        await page.Locator(".wb-tab").Filter(new() { HasText = "Tenant" }).First.ClickAsync();
        var view = page.Locator(".wb-tenant");
        await Expect(view).ToBeVisibleAsync();

        var clearanceSwitch = view.Locator(".wb-setting-row").Filter(new() { HasText = "Enforce sensitivity clearance" }).Locator("input");
        await Expect(clearanceSwitch).ToBeDisabledAsync();

        await view.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await Expect(clearanceSwitch).ToBeEnabledAsync();

        await view.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(clearanceSwitch).ToBeDisabledAsync();
    }
}
