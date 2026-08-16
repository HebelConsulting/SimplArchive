using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// User impersonation (ADR "User impersonation") via the real desktop api client: the demo admin (who holds
// CanImpersonate) creates a non-admin user, exchanges their token for one representing that user, and whoami
// through the impersonation token resolves the target + names the acting admin.
[Collection(UiCollection.Name)]
public class DesktopImpersonationTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopImpersonationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Exchange_impersonation_token_and_act_as_the_target()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var adminToken = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(adminToken);

        var targetId = await api.Admin.CreateUserAsync($"imp-target-{Guid.NewGuid():N}@e2e.local", "Imp Target");

        var impersonationToken = await SimplArchiveApiClient.ExchangeImpersonationTokenAsync(adminToken, targetId.Id);
        Assert.NotNull(impersonationToken);

        var whoami = await new SimplArchiveApiClient(impersonationToken!).GetWhoAmIAsync();
        Assert.Equal(targetId.Id, whoami.UserId);
        Assert.NotNull(whoami.ImpersonatedBy); // named the acting admin
    }
}
