using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The richer demo seed (issue #354): the Demo Repository has the Business Years / Contracts / General tree with
// varied document types, a two-version document, and a cross-folder reference. Verified over the real Api against
// the demo-seeded self-hosted app (the same seed the kiosk ships).
[Collection(UiCollection.Name)]
public class DesktopDemoTreeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDemoTreeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Demo_repository_has_the_business_tree_with_versions_and_a_reference()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(r => r.Name == "Demo Repository");
        var topLevel = (await api.GetChildrenAsync(repo.Href("children"))).Select(n => n.Name).ToList();
        Assert.Contains("Business Years", topLevel);
        Assert.Contains("Contracts", topLevel);
        Assert.Contains("General", topLevel);

        async Task<SimplArchiveApiClient.Node> ChildAsync(Guid parentId, string name) =>
            (await api.GetChildrenAsync(parentId)).Single(n => n.Name == name);

        // Business Years / 2026 / 03 March holds the chocolate-gift invoice with TWO versions (Compare-versions).
        var businessYears = await ChildAsync(repo.Id, "Business Years");
        var year = await ChildAsync(businessYears.Id, "2026");
        var march = await ChildAsync(year.Id, "03 March");
        var chocolate = await ChildAsync(march.Id, "Invoice for customer's chocolate gift");
        Assert.Equal(2, chocolate.VersionCount);

        // The March Telekom invoice lives under Contracts/MyCountry Telekom/Invoices and is referenced into a month
        // folder — so it reports HasReferences.
        var contracts = await ChildAsync(repo.Id, "Contracts");
        var telekom = await ChildAsync(contracts.Id, "MyCountry Telekom");
        var telekomInvoices = await ChildAsync(telekom.Id, "Invoices");
        var marchInvoice = await ChildAsync(telekomInvoices.Id, "MyCountry Telekom invoice — March 2026");
        Assert.True(marchInvoice.HasReferences);
    }
}
