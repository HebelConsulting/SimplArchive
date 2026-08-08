using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SimplArchive.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// OIDC via the standard Blazor WASM auth library (PKCE, token storage, silent renew, and the
// AuthenticationStateProvider/<AuthorizeView> integration all handled for it). Authority is the app's own
// origin — single-deployable, same-origin, no CORS — and relies entirely on OpenIddict's real
// /.well-known/openid-configuration discovery document rather than hardcoding /connect/authorize or
// /connect/token by hand. See ADR "Blazor Client-side login wiring". "blazor-client" is a public client
// (no secret), seeded idempotently by the Api itself on startup.
builder.Services.AddOidcAuthentication(options =>
{
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "blazor-client";
    options.ProviderOptions.ResponseType = "code";
    // The library pre-seeds DefaultScopes with "openid profile", but this OpenIddict server only registers
    // the "openid" scope — requesting "profile" makes /connect/authorize reject with invalid_scope (400),
    // which breaks both the silent sign-in on load and interactive login. Clear and request only "openid";
    // the email claim still reaches the id_token via AuthorizationController's per-claim SetDestinations
    // regardless of requested scope. See ADR "Blazor Client-side login wiring".
    options.ProviderOptions.DefaultScopes.Clear();
    options.ProviderOptions.DefaultScopes.Add("openid");
});

// The one HttpClient this app needs so far talks back to this same origin's own Api (e.g.
// /diagnostics/whoami) — AuthorizationMessageHandler attaches the access token automatically for calls to
// the configured authorized URL.
// Impersonation (ADR "User impersonation"): ImpersonationHandler is the inner handler — it overrides the admin
// token AuthorizationMessageHandler attaches with the impersonation token when a session is active.
// Singleton (not scoped): IHttpClientFactory resolves the message handler in a separate handler scope, so the
// handler and the ImpersonationService/MainLayout must share one instance to see the same token.
builder.Services.AddSingleton<SimplArchive.Client.Services.ImpersonationState>();
builder.Services.AddSingleton<SimplArchive.Client.Services.AppNavigationState>();
builder.Services.AddScoped<SimplArchive.Client.Services.ImpersonationHandler>();
builder.Services.AddScoped<SimplArchive.Client.Services.ImpersonationService>();

builder.Services.AddHttpClient("SimplArchive.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<SimplArchive.Client.Services.ImpersonationHandler>());
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("SimplArchive.Api"));

// The API root discovery document, fetched once and cached (ADR 0543, issue #416) — the client's single
// entry point, so screens follow rels instead of composing request paths by hand. (Worded without the
// literal path prefix on purpose: ClientHypermediaTests counts occurrences textually and does not strip
// comments, so writing the pattern out here would score against this file's budget.)
//
// SCOPED, not singleton, even though there is one user and one root: it depends on the scoped HttpClient,
// and a singleton holding a scoped dependency is captive. In Blazor WASM the distinction is invisible
// (one scope for the app's lifetime), which is exactly why it would be a trap for anyone reusing this
// against a scoped-per-request host.
builder.Services.AddScoped<SimplArchive.Client.Services.ApiRoot>();

// The UI language is applied at the WASM runtime level via Blazor.start({ applicationCulture }) in index.html
// (ADR "Web UI localization — shared resources") — set before the app runs, so the resx accessor resolves to
// that language on the first render (no live switch; the switcher persists the choice and reloads).
await builder.Build().RunAsync();
