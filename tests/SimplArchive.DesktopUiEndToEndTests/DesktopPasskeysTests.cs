using Npgsql;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop passkey list/remove path (ADR "Desktop passkey management"): these are plain API calls the native
// app makes directly (registration itself needs a browser ceremony and is delegated to the system browser, so
// it isn't exercised here). A WebAuthnCredential is seeded straight into the DB for a throwaway user, then the
// real desktop api client lists it and removes it.
[Collection(UiCollection.Name)]
public class DesktopPasskeysTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopPasskeysTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task List_and_remove_passkeys()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"passkey-{suffix}@example.test";

        var userId = await admin.CreateUserAsync(email, "Passkey User " + suffix);
        var password = await admin.ResetUserPasswordAsync(userId);
        var user = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, email, password));

        // No passkeys to start.
        Assert.Empty(await user.GetPasskeysAsync());

        // Seed a credential row directly (a registration needs a browser ceremony we can't run here).
        var passkeyId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(_app.PostgresConnectionString))
        {
            await conn.OpenAsync();
            Guid tenantId;
            await using (var q = new NpgsqlCommand("SELECT \"TenantId\" FROM \"Users\" WHERE \"Id\" = @uid", conn))
            {
                q.Parameters.AddWithValue("uid", userId.Id);
                tenantId = (Guid)(await q.ExecuteScalarAsync())!;
            }

            await using var insert = new NpgsqlCommand(
                "INSERT INTO \"WebAuthnCredentials\" (\"Id\",\"TenantId\",\"UserId\",\"CredentialId\",\"PublicKey\",\"SignCount\",\"AaGuid\",\"Transports\",\"Name\",\"CreatedAt\") " +
                "VALUES (@id,@tid,@uid,@cred,@pk,0,@aaguid,NULL,@name,now())", conn);
            insert.Parameters.AddWithValue("id", passkeyId);
            insert.Parameters.AddWithValue("tid", tenantId);
            insert.Parameters.AddWithValue("uid", userId.Id);
            insert.Parameters.AddWithValue("cred", Guid.NewGuid().ToByteArray());
            insert.Parameters.AddWithValue("pk", new byte[] { 1, 2, 3, 4 });
            insert.Parameters.AddWithValue("aaguid", Guid.Empty);
            insert.Parameters.AddWithValue("name", "Seeded Key");
            await insert.ExecuteNonQueryAsync();
        }

        // The api client lists it, then removes it.
        var passkeys = await user.GetPasskeysAsync();
        var seeded = Assert.Single(passkeys);
        Assert.Equal("Seeded Key", seeded.Name);
        Assert.Equal(passkeyId, seeded.Id);

        // Removal follows the row's own `self` rel rather than a path rebuilt from the id (ADR 0543, issue
        // #416) — so asserting the rel arrived is part of asserting removal works at all.
        Assert.NotNull(seeded.RemoveHref);
        await user.RemovePasskeyAsync(seeded.RemoveHref!);
        Assert.Empty(await user.GetPasskeysAsync());
    }
}
