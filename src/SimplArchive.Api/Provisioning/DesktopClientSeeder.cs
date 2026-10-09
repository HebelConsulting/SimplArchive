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
/// <b>Healed, not merely created-if-absent</b>, by <see cref="PublicClientSeeder"/>, which the mobile app's client
/// shares (ADR 0916).
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

    public static Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default) =>
        PublicClientSeeder.SeedAsync(applications, new PublicClientRegistration(ClientId, Permissions, [SimplArchive.Api.Security.DesktopLoopback.RedirectUri]), cancellationToken);
}
