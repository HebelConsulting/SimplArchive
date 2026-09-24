using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// Seeds the OpenIddict client for <c>saconsole</c>, the administrative CLI (ADRs 0822/0823).
/// </summary>
/// <remarks>
/// Its own file rather than another block in <c>Program.cs</c>: adding it inline took that file to 1019 lines
/// and the 1000-line guard refused, which is the guard doing its job — an exception there is the owner's to
/// grant, and "one more client" is exactly the kind of growth the rule exists to stop being automatic.
/// </remarks>
public static class SaConsoleClientSeeder
{
    public const string ClientId = "saconsole";

    /// <summary>Idempotent: creates the client when it is absent, and leaves an existing one alone.</summary>
    public static async Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default)
    {
        if (await applications.FindByClientIdAsync(ClientId, cancellationToken) is not null)
        {
            return;
        }

        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,

            // A PUBLIC client with no redirect URI at all: the device grant never redirects, which is the
            // whole reason it works over SSH where the desktop client's loopback cannot.
            //
            // Explicit, NOT implicit consent: a device grant hands a token to a machine the approving person
            // may not be sitting at, so the approval has to be an act rather than an inference.
            ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
            DisplayName = "SimplArchive administrative CLI (saconsole)",
            Permissions =
            {
                // No verification-endpoint permission exists, and that is not an omission: enumerated from the
                // assembly, Permissions.Endpoints offers Authorization, DeviceAuthorization, EndSession,
                // Introspection, PushedAuthorization, Revocation and Token only. The verification endpoint is
                // where a PERSON acts, not where a client authenticates, so there is nothing to grant a client
                // there. Written down because it cost two wrong guesses to learn.
                OpenIddictConstants.Permissions.Endpoints.DeviceAuthorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.DeviceCode,

                // "openid" and nothing else. An `email` permission was here and was a promise the server
                // cannot keep: AddAuthServer registers only openid and offline_access, so a client granted
                // `email` and honest enough to ASK for it is answered invalid_scope — which reads as a broken
                // client rather than as a missing RegisterScopes call. Nothing is lost by dropping it,
                // because the email claim does not ride on that scope: the verification page puts it on the
                // access and identity tokens unconditionally, exactly as AuthorizationController does.
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
            },
        }, cancellationToken);
    }
}
