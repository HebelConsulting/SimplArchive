using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of sensitivity clearance enforcement (ADR "Sensitivity clearance enforcement"): the real
// SimplArchiveApiClient round-trips the tenant EnforceClearance switch and a principal's ClearanceRank (set in
// the Users & groups rights bundle). The gating behaviour itself is covered by the E2E + integration tests.
[Collection(UiCollection.Name)]
public class DesktopSensitivityClearanceTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSensitivityClearanceTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tenant_switch_and_principal_clearance_round_trip()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A user gets clearance 2 via the rights bundle; it reads back on the user resource.
        var userId = await client.Admin.CreateUserAsync($"clr-{suffix}@example.test", "clr-user-" + suffix);
        await client.Admin.SetRightsAsync(userId, new AdminClient.SystemRightsData(
            false, false, false, false, false, false, false, false, false, false, false, false, false, ClearanceRank: 2));

        var user = (await client.Admin.GetUsersAsync()).Single(u => u.Id == userId.Id);
        Assert.Equal(2, user.Rights.ClearanceRank);

        // The tenant EnforceClearance switch round-trips through the Tenant tab api (leave it OFF so the shared
        // demo tenant is unaffected for other tests).
        var before = await client.Admin.GetTenantSettingsAsync();
        var on = await client.Admin.SaveTenantSettingsGroupAsync(before, "security", new { requireMfa = before.RequireMfa, allowPasskeyLogin = before.AllowPasskeyLogin, enforceClearance = true });
        Assert.True(on.EnforceClearance);

        var off = await client.Admin.SaveTenantSettingsGroupAsync(before, "security", new { requireMfa = before.RequireMfa, allowPasskeyLogin = before.AllowPasskeyLogin, enforceClearance = false });
        Assert.False(off.EnforceClearance);
    }
}
