using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OtpNet;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising two-factor auth (ADR "MFA (interactive login, TOTP)"):
// enroll → enable with a computed TOTP → the login now requires + accepts a TOTP → a recovery code also works →
// self-disable and admin-reset both clear it.
[Collection(E2ECollection.Name)]
public class MfaTests
{
    private readonly E2EApiFactory _factory;

    public MfaTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Enroll_enable_and_login_with_totp_then_recovery_then_disable()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(false);
        var email = $"mfa-{Guid.NewGuid():N}@e2e.local";
        const string password = "mfa-pass-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "MFA User");
        var adminEmail = $"mfaadmin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "MFA Admin", canResetMfa: true);

        // Password-only login works while MFA is off.
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Enroll → secret; enable with a code computed from it → recovery codes.
        var enroll = await (await user.PostAsync("/api/users/me/mfa/enroll", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = enroll.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrEmpty(enroll.GetProperty("qrDataUrl").GetString()));
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        var enableResp = await user.PostAsJsonAsync("/api/users/me/mfa/enable", new { code = totp.ComputeTotp() });
        enableResp.EnsureSuccessStatusCode();
        var recoveryCodes = (await enableResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recoveryCodes.Count);

        // whoami now reports MFA enabled.
        Assert.True((await Whoami(user)).GetProperty("mfaEnabled").GetBoolean());

        // A wrong code at enable-time would have failed.
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/users/me/mfa/enable", new { code = "000000" })).StatusCode);

        // A fresh login now requires the second factor and accepts a current TOTP.
        var totpToken = await _factory.GetUserTokenAsync(email, password, () => totp.ComputeTotp());
        Assert.False(string.IsNullOrEmpty(totpToken));

        // A recovery code also logs in (single-use).
        var recoveryToken = await _factory.GetUserTokenAsync(email, password, () => recoveryCodes[0]);
        Assert.False(string.IsNullOrEmpty(recoveryToken));

        // Self-disable (using an MFA-authenticated session) turns it back off.
        using var mfaUser = _factory.CreateAuthedClient(totpToken);
        (await mfaUser.DeleteAsync("/api/users/me/mfa")).EnsureSuccessStatusCode();
        Assert.False((await Whoami(mfaUser)).GetProperty("mfaEnabled").GetBoolean());

        // Password-only login works again after disabling.
        Assert.False(string.IsNullOrEmpty(await _factory.GetUserTokenAsync(email, password)));

        // Re-enroll, then an admin with CanResetMfa clears it.
        using var user2 = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var secret2 = (await (await user2.PostAsync("/api/users/me/mfa/enroll", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        (await user2.PostAsJsonAsync("/api/users/me/mfa/enable", new { code = new Totp(Base32Encoding.ToBytes(secret2)).ComputeTotp() })).EnsureSuccessStatusCode();

        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));
        (await admin.PostAsync($"/api/users/{userId}/mfa/reset", null)).EnsureSuccessStatusCode();

        // After reset, password-only login works and MFA is off.
        Assert.False((await Whoami(_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)))).GetProperty("mfaEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_non_admin_cannot_reset_another_users_mfa()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(false);
        const string password = "mfa-pass-1234";
        var targetId = await _factory.SeedUserAsync(tenantId, $"t-{Guid.NewGuid():N}@e2e.local", password, "Target");
        var plainEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain"); // no CanResetMfa

        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"/api/users/{targetId}/mfa/reset", null)).StatusCode);
    }

    private static async Task<JsonElement> Whoami(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/diagnostics/whoami");
}
