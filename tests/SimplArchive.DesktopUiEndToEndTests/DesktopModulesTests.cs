using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The Tenant tab's Modules surface (ADRs 0740/0743) via the real desktop api client: the settings resource
// advertises the modules rel to the demo admin, the catalog answers (empty — this host stages no module
// assemblies), and the list's license-documents rel serves the Activate dialog's dropdown. The full
// activation circle (a staged module + a signed license) is exercised at the integration seam
// (ModuleActivationTests); staging a module assembly into the self-hosted Api is its own follow-up.
[Collection(UiCollection.Name)]
public class DesktopModulesTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopModulesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_modules_surface_is_advertised_and_answers()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var settings = await api.Admin.GetTenantSettingsAsync();
        var catalog = await api.Admin.GetModulesAsync(settings);

        // The rel is advertised to a tenant admin, and the catalog answers even with nothing installed —
        // an empty list is an answer, not an absence (the withhold-a-rel rule).
        Assert.NotNull(catalog);
        Assert.Empty(catalog!.Items);

        // The dropdown's source is advertised where the collection was read (ADR 0557) — and it answers
        // too: no documents wear the Module-license mask in the demo seed.
        Assert.NotNull(catalog.LicenseDocumentsHref);
        Assert.Empty(await api.Admin.GetLicenseDocumentsAsync(catalog.LicenseDocumentsHref!));
    }
}
