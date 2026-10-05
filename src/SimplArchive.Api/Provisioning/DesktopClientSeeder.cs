using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// Seeds — and HEALS — the desktop client's OpenIddict application: a public client using Authorization Code + PKCE
/// with a fixed loopback redirect (RFC 8252). See ADR "Cross-platform desktop fat client (Avalonia)".
/// </summary>
/// <remarks>
/// <para>
/// Its own file rather than a block in <c>Program.cs</c>, which was at 992 lines when the revocation permission
/// needed adding — the same reason <see cref="SaConsoleClientSeeder"/> moved out.
/// </para>
/// <para>
/// <b>Healed, not merely created-if-absent.</b> A create-if-null seed gives new permissions only to deployments
/// that never had the client, so an existing install keeps the old set for ever and the new grant is refused with
/// <c>unauthorized_client</c> — which reads as a client bug rather than as a stale registration. Same shape as the
/// well-known mask heal (#579) and the trap #664 recorded. The heal ADDS what is missing from
/// <see cref="Permissions"/> and removes nothing, so a permission an operator granted by hand survives.
/// </para>
/// </remarks>
public static class DesktopClientSeeder
{
    public const string ClientId = "simplarchive-desktop";

    /// <summary>Everything the desktop needs; the heal brings an existing registration up to this set.</summary>
    internal static readonly string[] Permissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Scopes.Email,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
        // Renewal without the user present: the grant, and the scope that asks for a refresh token.
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        // RFC 8693 token exchange for User impersonation (ADR "User impersonation").
        OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType,
        // RFC 7009: sign-out revokes the session's tokens on the server (#1578, ADR 0895).
        OpenIddictConstants.Permissions.Endpoints.Revocation,
    ];

    public static async Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default)
    {
        if (await applications.FindByClientIdAsync(ClientId, cancellationToken) is { } existing)
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, existing, cancellationToken);

            var missing = Permissions.Where(p => !descriptor.Permissions.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                descriptor.Permissions.UnionWith(missing);
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
            }

            return;
        }

        var created = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            RedirectUris = { new Uri(SimplArchive.Api.Security.DesktopLoopback.RedirectUri) },
            Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange },
        };
        created.Permissions.UnionWith(Permissions);
        await applications.CreateAsync(created, cancellationToken);
    }
}
