using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The tenant-admin Administration → Users view (ADR "Tenant-admin Administration → Users view") through the real
// DesktopClient api client: a tenant admin lists every user's personal repository and can browse into one (via the
// IsTenantAdmin ACL bypass), even a throwaway user's freshly-created personal space.
[Collection(UiCollection.Name)]
public class DesktopAdminUsersTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminUsersTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Admin_lists_and_browses_a_users_personal_repository()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A throwaway user with a personal repository.
        var userId = await admin.CreateUserAsync($"au-{suffix}@example.test", "AdminView User " + suffix);
        var password = await admin.ResetUserPasswordAsync(userId);
        var user = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"au-{suffix}@example.test", password));
        var userRepo = await user.GetPersonalRepositoryAsync();
        Assert.NotNull(userRepo);

        // The admin lists every personal repository — the throwaway user's is present.
        var repos = await admin.GetAdminPersonalRepositoriesAsync();
        Assert.Contains(repos, r => r.RepositoryId == userRepo!.Id && r.UserId == userId.Id);

        // The admin can browse into it (empty, but reachable via the ACL bypass).
        var children = await admin.GetChildrenAsync(userRepo!.Id);
        Assert.NotNull(children);
    }
}
