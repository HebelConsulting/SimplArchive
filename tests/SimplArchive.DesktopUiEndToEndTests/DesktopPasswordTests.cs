using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Password management (ADR "User password management") end to end via the real api client + the real OIDC
// login, on a throwaway user (so the shared demo admin's password is never touched): admin reset returns a
// working password the user can log in with, then the user changes it via self-service (the old one stops
// working, the new one works).
[Collection(UiCollection.Name)]
public class DesktopPasswordTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopPasswordTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Admin_reset_then_self_service_change()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"pw-{suffix}@example.test";

        // A throwaway user, then an admin reset → the returned password logs them in.
        var userId = await admin.Admin.CreateUserAsync(email, "PW User " + suffix);
        var reset = await admin.Admin.ResetUserPasswordAsync(userId);
        Assert.NotEmpty(reset);

        var userToken = await Ui.GetUserTokenAsync(_app.BaseUrl, email, reset);
        var user = new SimplArchiveApiClient(userToken);

        // Self-service change: wrong current password is rejected; the correct one works.
        await Assert.ThrowsAsync<ApiActionException>(() => user.ChangeMyPasswordAsync("not-the-password", "Whatever1!"));

        var newPassword = "New-" + suffix + "-Pw1!";
        await user.ChangeMyPasswordAsync(reset, newPassword);

        // The old password no longer logs in; the new one does.
        await Assert.ThrowsAnyAsync<Exception>(() => Ui.GetUserTokenAsync(_app.BaseUrl, email, reset));
        var reloginToken = await Ui.GetUserTokenAsync(_app.BaseUrl, email, newPassword);
        Assert.NotEmpty(reloginToken);
    }
}
