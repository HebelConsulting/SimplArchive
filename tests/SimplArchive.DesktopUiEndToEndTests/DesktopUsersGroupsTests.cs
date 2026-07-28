using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the Users & groups feature (ADR "Users & groups administration tab"): the real
// DesktopClient SimplArchiveApiClient drives create / assign-rights / read-back / delete against the running
// API. Verifies the desktop api-client wiring end to end (the XAML/VM is exercised by the --users headless
// screenshot). The escalation cap itself is a server concern (SystemRightsPolicy) covered by the web UI +
// integration tests; the founding tenant admin now holds every system right (ADR "Desktop recycle bin
// parity" / 0329), so it can grant any of them — including Impersonate.
[Collection(UiCollection.Name)]
public class DesktopUsersGroupsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopUsersGroupsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Create_assign_rights_read_back_delete_and_escalation_cap()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // whoami exposes CanManageUsers — this is what gates the desktop tab's visibility.
        Assert.True((await client.GetWhoAmIAsync()).CanManageUsers);

        // Create a group and grant it "Manage masks" (a right the demo admin holds), then read it back.
        var groupId = await client.CreateGroupAsync("dt-group-" + suffix);
        await client.SetGroupRightsAsync(groupId, Rights(canManageMasks: true));

        var group = (await client.GetGroupsAsync()).Single(g => g.Id == groupId);
        Assert.True(group.IsGroup);
        Assert.True(group.Rights.CanManageMasks);
        Assert.False(group.Rights.CanImpersonate);

        // Create a user too — it shows up (not a group).
        var userId = await client.CreateUserAsync($"dt-{suffix}@example.test", "dt-user-" + suffix);
        var user = (await client.GetUsersAsync()).Single(u => u.Id == userId);
        Assert.False(user.IsGroup);
        Assert.True(user.IsActive);

        // The founding tenant admin holds every system right, so it can grant Impersonate — the assignment
        // round-trips through the desktop api client and reads back.
        await client.SetUserRightsAsync(userId, Rights(canImpersonate: true));
        Assert.True((await client.GetUsersAsync()).Single(u => u.Id == userId).Rights.CanImpersonate);

        // Delete the group → it's gone from the list.
        await client.DeleteGroupAsync(groupId);
        Assert.DoesNotContain(await client.GetGroupsAsync(), g => g.Id == groupId);
    }

    private static SimplArchiveApiClient.SystemRightsData Rights(bool canManageMasks = false, bool canImpersonate = false) =>
        new(false, canImpersonate, false, false, false, false, false, canManageMasks, false, false, false, false, false);
}
