using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of service-account management (ADR 0534): the real DesktopClient SimplArchiveApiClient drives
// the whole lifecycle — create (one-time secret) / list / edit-rights (PUT) / rotate-secret / revoke — against
// the running API. Verifies the desktop api-client wiring end to end (the XAML/VM orchestration is the window's
// job). The founding tenant admin holds every system right, so it can grant CanExport/CanImport here.
[Collection(UiCollection.Name)]
public class DesktopServiceAccountsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopServiceAccountsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_list_edit_rotate_and_revoke()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // whoami exposes CanManageServiceAccounts — this gates the desktop manager button's visibility.
        Assert.True((await client.GetWhoAmIAsync()).CanManageServiceAccounts);

        // Create with CanExport → a one-time client_id + client_secret comes back.
        var name = "dt-sa-" + suffix;
        var secret = await client.CreateServiceAccountAsync(name, Rights(canExport: true));
        Assert.NotEmpty(secret.ClientId);
        Assert.NotEmpty(secret.ClientSecret);

        // It's listed, active, and carries the granted right.
        var created = (await client.GetServiceAccountsAsync()).Single(a => a.Name == name);
        Assert.True(created.IsActive);
        Assert.True(created.CanExport);
        Assert.False(created.CanImport);

        // Edit (PUT): rename + add CanImport → reads back.
        var newName = "dt-sa-edited-" + suffix;
        await client.UpdateServiceAccountAsync(created.Id, newName, Rights(canExport: true, canImport: true));
        var edited = (await client.GetServiceAccountsAsync()).Single(a => a.Id == created.Id);
        Assert.Equal(newName, edited.Name);
        Assert.True(edited.CanImport);

        // Rotate the secret → a fresh one-time secret.
        var rotated = await client.RotateServiceAccountSecretAsync(created.Id);
        Assert.NotEmpty(rotated.ClientSecret);
        Assert.NotEqual(secret.ClientSecret, rotated.ClientSecret);

        // Revoke → still listed, but inactive (one-way).
        await client.RevokeServiceAccountAsync(created.Id);
        var revoked = (await client.GetServiceAccountsAsync()).Single(a => a.Id == created.Id);
        Assert.False(revoked.IsActive);
    }

    private static SimplArchiveApiClient.SystemRightsData Rights(bool canExport = false, bool canImport = false) =>
        new(false, false, false, false, false, false, false, false, false, false, false, canExport, canImport);
}
