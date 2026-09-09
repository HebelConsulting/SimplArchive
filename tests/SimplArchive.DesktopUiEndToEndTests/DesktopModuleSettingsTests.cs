using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of per-tenant module configuration (ADR 0772). The desktop is canonical (ADR 0511), so
// this is where the behaviour is pinned and the web mirrors it.
//
// View-model level, deliberately: what the dialog renders is decided entirely by the declarations the server
// returned, and the interesting rules are all about a SECRET — that its box starts empty even when one is
// configured, and that the watermark is what stops an empty box reading as "not set".
public class DesktopModuleSettingsTests
{
    [Fact]
    public void A_secret_starts_empty_with_a_watermark_and_a_plain_value_is_prefilled()
    {
        var configuredSecret = Entry(new AdminClient.ModuleSettingInfo(
            "apiSecret", "API secret", Description: null, IsSecret: true, HasValue: true, Value: null));
        var endpoint = Entry(new AdminClient.ModuleSettingInfo(
            "endpoint", "Endpoint", Description: "Where to call.", IsSecret: false, HasValue: true,
            Value: "https://example.test"));

        // The value never crossed the wire, so there is nothing to prefill — and the watermark is what says
        // the empty box means "configured, leave it" rather than "nothing here".
        Assert.Equal(string.Empty, configuredSecret.Entry);
        Assert.NotEqual(string.Empty, configuredSecret.Watermark);
        Assert.True(configuredSecret.HasValue);

        // A plain value IS prefilled, or the form could not show what it is about to change.
        Assert.Equal("https://example.test", endpoint.Entry);
        Assert.Equal(string.Empty, endpoint.Watermark);
        Assert.True(endpoint.HasDescription);
    }

    [Fact]
    public void An_unconfigured_secret_has_no_watermark()
    {
        // Nothing is set, so there is nothing to promise is being kept — an empty box here means empty.
        var fresh = Entry(new AdminClient.ModuleSettingInfo(
            "apiSecret", "API secret", Description: null, IsSecret: true, HasValue: false, Value: null));

        Assert.Equal(string.Empty, fresh.Watermark);
    }

    [Fact]
    public void A_secret_is_masked_as_it_is_typed_and_a_plain_value_is_not()
    {
        var secret = Entry(new AdminClient.ModuleSettingInfo(
            "apiSecret", "API secret", null, IsSecret: true, HasValue: false, Value: null));
        var plain = Entry(new AdminClient.ModuleSettingInfo(
            "endpoint", "Endpoint", null, IsSecret: false, HasValue: false, Value: null));

        Assert.NotEqual('\0', secret.PasswordChar);
        Assert.Equal('\0', plain.PasswordChar);   // Avalonia's "no masking"
    }

    // The affordance follows the server's answer (ADR 0543): a module that declares no settings gets no rel,
    // and therefore no button — rather than a button opening an empty form.
    [Fact]
    public void The_configure_button_follows_the_rel()
    {
        Assert.True(Row(settingsHref: "https://example.test/api/modules/x/settings").CanConfigure);
        Assert.False(Row(settingsHref: null).CanConfigure);
    }

    private static ModuleSettingEntryViewModel Entry(AdminClient.ModuleSettingInfo setting) => new(setting);

    private static ModuleRowViewModel Row(string? settingsHref) =>
        new(new AdminClient.ModuleInfo(
            "test-module", "Test Module", Installed: true, Activated: true, Active: true, InGrace: false,
            SupportContractEndDate: DateTimeOffset.UtcNow.AddYears(1), DeactivatesAt: null,
            LicenseHref: "https://example.test/api/modules/x/license", SettingsHref: settingsHref));
}
