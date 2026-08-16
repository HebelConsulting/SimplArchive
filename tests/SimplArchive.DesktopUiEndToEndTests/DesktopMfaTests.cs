using OtpNet;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Two-factor auth (ADR "MFA (interactive login, TOTP)") end to end via the real desktop api client + the real
// OIDC login, on a throwaway user (so the shared demo admin is never given MFA): enroll → a wrong code is
// rejected → enable with a computed TOTP returns recovery codes and flips whoami.mfaEnabled → self-disable
// clears it. (Admin reset needs CanResetMfa, which the demo admin can't self-grant, so it's covered by the
// container E2E MfaTests instead.)
[Collection(UiCollection.Name)]
public class DesktopMfaTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopMfaTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Enroll_enable_and_disable()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"mfa-{suffix}@example.test";

        // A throwaway user with a known password (via admin create + reset), then log in as them.
        var userId = await admin.Admin.CreateUserAsync(email, "MFA User " + suffix);
        var password = await admin.Admin.ResetUserPasswordAsync(userId);
        var user = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, email, password));

        // Enroll → a secret + QR; a wrong code is rejected; the right one enables MFA + returns recovery codes.
        var enroll = await user.EnrollMfaAsync();
        Assert.NotEmpty(enroll.Secret);
        Assert.StartsWith("data:image/png;base64,", enroll.QrDataUrl);

        await Assert.ThrowsAsync<ApiActionException>(() => user.EnableMfaAsync("000000"));

        var totp = new Totp(Base32Encoding.ToBytes(enroll.Secret));
        var recoveryCodes = await user.EnableMfaAsync(totp.ComputeTotp());
        Assert.Equal(10, recoveryCodes.Count);
        Assert.True((await user.GetWhoAmIAsync()).MfaEnabled);

        // Self-disable turns it back off.
        await user.DisableMfaAsync();
        Assert.False((await user.GetWhoAmIAsync()).MfaEnabled);
    }
}
