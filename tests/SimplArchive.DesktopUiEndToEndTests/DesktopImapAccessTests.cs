using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the IMAP account surface (ADR 0594, #562): the ProfileClient area the ImapDialog rides —
// status via the me rel, generate (password shown once), the self-service view toggle, and revoke, all through
// the resource's own advertised rels.
[Collection(UiCollection.Name)]
public class DesktopImapAccessTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopImapAccessTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Imap_access_generates_toggles_and_revokes_through_advertised_rels()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var status = await api.Profile.GetImapAccessAsync();
        Assert.True(status.Available); // the fixture runs the endpoint on an ephemeral port
        Assert.NotEmpty(status.Username);

        var generated = await api.Profile.GenerateImapPasswordAsync(status);
        Assert.True(generated.Enabled);
        Assert.Matches("^[0-9a-f]{32}$", generated.Password!);

        await api.Profile.SetImapShowAllDocumentsAsync(generated, showAllDocuments: true);
        Assert.True((await api.Profile.GetImapAccessAsync()).ShowAllDocuments);
        await api.Profile.SetImapShowAllDocumentsAsync(generated, showAllDocuments: false);

        await api.Profile.RevokeImapPasswordAsync(generated);
        Assert.False((await api.Profile.GetImapAccessAsync()).Enabled);
    }
}
