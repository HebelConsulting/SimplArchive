using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Auth;

public static class DependencyInjection
{
    /// <summary>
    /// Registers OpenIddict as the solution's sole token issuer (see ADR: Planned authentication).
    /// Assumes `AddInfrastructure` has already registered SimplArchiveDbContext.
    ///
    /// Both the client-credentials grant (ServiceAccount/PlatformAdministrator) and the interactive
    /// Authorization Code + PKCE grant (User, see ADR "Interactive User login (foundation slice)") are
    /// wired up. The "openid" scope/identity token are now registered too (ADR "Blazor Client-side login
    /// wiring" — the standard Blazor WASM auth library needs real OIDC, not just an access token, to build
    /// a ClaimsPrincipal), superseding ADR 0208's original "plain OAuth 2.0 only" stance. Deliberately
    /// still NOT implemented: a userinfo endpoint or RP-initiated logout, MFA (see MFA / authentication
    /// credential policy), the custom impersonation token-exchange grant (RFC 8693 — see Admin user
    /// impersonation), and rotating-refresh-token reuse detection (see Session / token lifetime and
    /// refresh strategy) — each is real, separate work.
    /// </summary>
    public static IServiceCollection AddAuthServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Sets OpenIddict's own validation handler as the default authentication scheme, so a plain
        // [Authorize] (no AuthenticationSchemes specified) resolves correctly on every protected endpoint
        // — without this, ASP.NET Core has no default scheme to challenge against and every [Authorize]
        // request fails with an InvalidOperationException instead of a 401. See ADR "ServiceAccount
        // request authentication foundation". Cookie authentication is also registered, but NOT as the
        // default scheme — it's used only by the interactive login page/authorization endpoint, entirely
        // separate from bearer-token validation on every other Api call. See ADR "Interactive User login
        // (foundation slice)".
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
            });

        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<SimplArchiveDbContext>();
            })
            .AddServer(options =>
            {
                options.SetTokenEndpointUris("connect/token");
                options.SetAuthorizationEndpointUris("connect/authorize");
                // The standard Blazor WASM auth library (oidc-client) defaults to loadUserInfo=true, so
                // after the code exchange it calls the userinfo endpoint — without one advertised in the
                // discovery document it aborts sign-in with "There was an error signing in." See
                // SimplArchive.Api's UserInfoController and ADR "Blazor Client-side login wiring".
                options.SetUserInfoEndpointUris("connect/userinfo");

                options.AllowClientCredentialsFlow();

                // PKCE is required unconditionally for the Authorization Code flow — the Blazor Client is
                // a public client (no client secret), so proof-of-possession is the only protection
                // against authorization-code interception.
                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange();

                // RFC 8693 token exchange, used only for User impersonation (ADR "User impersonation") — a
                // CanImpersonate admin exchanges their access token for one representing a target User.
                options.AllowCustomFlow(ImpersonationConstants.TokenExchangeGrantType);

                // "openid" is a well-known scope — RegisterScopes is a lightweight in-memory declaration,
                // not a DB-backed scope entity (no IOpenIddictScopeManager round-trip needed). Its
                // presence on a token request is what makes OpenIddict actually issue an identity token —
                // see ADR "Blazor Client-side login wiring".
                options.RegisterScopes(OpenIddictConstants.Scopes.OpenId);

                // Signing/encryption certificates: sourced from OpenBao when configured (ADR "OpenIddict
                // certificates from OpenBao" — the OpenBao config provider maps the PKI-issued cert/key PEMs to
                // these config keys), else the dev-only ephemeral certificates. Real deployments must run with
                // OpenBao (or another sourced cert), never the dev certs.
                var signingCertPem = configuration["OpenIddict:SigningCertificatePem"];
                var signingKeyPem = configuration["OpenIddict:SigningKeyPem"];
                var encryptionCertPem = configuration["OpenIddict:EncryptionCertificatePem"];
                var encryptionKeyPem = configuration["OpenIddict:EncryptionKeyPem"];
                if (configuration.GetValue<bool>("OpenIddict:UseEphemeralKeys"))
                {
                    // Hermetic in-memory signing/encryption keys — used only by the test hosts (E2E + UI E2E). The
                    // dev certificates persist to a per-user X.509 store, which fails in a headless CI runner
                    // environment (a different HOME / no interactive session); ephemeral keys need no store and are
                    // fine for a within-run token round-trip. NEVER set in a real deployment.
                    options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                }
                else if (!string.IsNullOrWhiteSpace(signingCertPem) && !string.IsNullOrWhiteSpace(signingKeyPem)
                    && !string.IsNullOrWhiteSpace(encryptionCertPem) && !string.IsNullOrWhiteSpace(encryptionKeyPem))
                {
                    options.AddSigningCertificate(OpenIddictCertificateLoader.FromPem(signingCertPem, signingKeyPem))
                        .AddEncryptionCertificate(OpenIddictCertificateLoader.FromPem(encryptionCertPem, encryptionKeyPem));
                }
                else
                {
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                }

                // Passthrough is required for both endpoints: OpenIddict has no principal to build a
                // token from on its own for client-credentials (there's no user to authenticate), and the
                // authorization endpoint needs a custom controller to challenge the cookie scheme and
                // show the login page — see SimplArchive.Api's TokenController/AuthorizationController.
                var aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();

                // OpenIddict requires HTTPS by default (a secure-by-default behavior we keep everywhere
                // except local development, where the API commonly runs over plain HTTP). Real
                // deployments terminate TLS in front of the API and must never disable this.
                if (environment.IsDevelopment())
                {
                    aspNetCoreBuilder.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        return services;
    }
}
