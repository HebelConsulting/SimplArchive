using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>A native app's public client: Authorization Code + PKCE, implicit consent, and what it may do.</summary>
public sealed record PublicClientRegistration(string ClientId, IReadOnlyList<string> Permissions, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Seeds and HEALS a native app's public client: the desktop's and the mobile app's (ADR 0916). One implementation
/// for both (ADR 0877); the clients differ only in data.
/// </summary>
/// <remarks>
/// <b>Healed, not merely created-if-absent.</b> A create-if-null seed gives new permissions only to deployments that
/// never had the client, so an existing install keeps the old set for ever. The new grant is then refused with
/// <c>unauthorized_client</c>, which reads as a client bug rather than a stale registration (#579, #664). The heal
/// ADDS what is missing, permissions and redirect URIs alike, and removes nothing, so what an operator granted by hand
/// survives. The desktop's seeder once healed permissions only, so a changed redirect would never have reached an
/// existing installation.
/// </remarks>
public static class PublicClientSeeder
{
    public static async Task SeedAsync(IOpenIddictApplicationManager applications, PublicClientRegistration client, CancellationToken cancellationToken = default)
    {
        var redirects = client.RedirectUris.Select(u => new Uri(u)).ToList();
        if (await applications.FindByClientIdAsync(client.ClientId, cancellationToken) is { } existing)
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, existing, cancellationToken);

            var missingPermissions = client.Permissions.Where(p => !descriptor.Permissions.Contains(p)).ToList();
            var missingRedirects = redirects.Where(r => !descriptor.RedirectUris.Contains(r)).ToList();
            if (missingPermissions.Count > 0 || missingRedirects.Count > 0)
            {
                descriptor.Permissions.UnionWith(missingPermissions);
                descriptor.RedirectUris.UnionWith(missingRedirects);
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
            }

            return;
        }

        var created = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange },
        };
        created.RedirectUris.UnionWith(redirects);
        created.Permissions.UnionWith(client.Permissions);
        await applications.CreateAsync(created, cancellationToken);
    }
}
