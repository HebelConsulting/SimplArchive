using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// Seeds and heals the mobile app's public client (ADR 0916): Authorization Code + PKCE to the private-use scheme
/// <see cref="SimplArchive.Api.Security.MobileRedirect.RedirectUri"/>, refresh tokens and revocation, as the desktop
/// has. No token exchange: impersonation stays a desktop act.
/// </summary>
/// <remarks>The client id is self-asserted by a public client, so it gates nothing (ADR 0866); the server treats every
/// user client alike, and content doors follow the tenant's mode and the reader's certificates (ADR 0865).</remarks>
public static class MobileClientSeeder
{
    public const string ClientId = "simplarchive-mobile";

    internal static readonly string[] Permissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Scopes.Email,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        OpenIddictConstants.Permissions.Endpoints.Revocation,
    ];

    public static Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default) =>
        PublicClientSeeder.SeedAsync(applications, new PublicClientRegistration(ClientId, Permissions, [SimplArchive.Api.Security.MobileRedirect.RedirectUri]), cancellationToken);
}
