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

    // NO "offline_access", and therefore no refresh token — a DECISION, not an omission (ADR 0660, #669).
    // This client renews by re-authorizing against the OpenIddict cookie (`prompt=none`), where the desktop
    // rotates a refresh token held in the OS secret store. The desktop's credential is reachable by the user's
    // account; a browser's would sit in the same storage an XSS already reads, upgrading the blast radius from
    // one hour of access to long-lived offline access — with reuse detection still deferred.
    //
    // The trigger that revisits it: federation (#545). Silent renew works because client and auth server are
    // same-origin, so third-party-cookie rules do not apply; an external IdP is where that stops holding.
    // WebSilentRenewTests fails if this line grows offline_access, which is how the decision stays a decision.
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

// The Search tab's on-screen state. Held outside the component because the workbench renders one tab at a time
// and opening a hit switches to Repositories — so results kept in the component would vanish exactly when the
// user comes back for the next hit (ADRs 0511/0558; see SearchState).
builder.Services.AddScoped<SimplArchive.Client.Services.SearchState>();

// The tenant's sensitivity labels, shared by the Repositories detail pane (as a picker) and the Users & groups
// tab (rank → name). One loader rather than one per surface — see SensitivityLabelCatalog.
builder.Services.AddScoped<SimplArchive.Client.Services.SensitivityLabelCatalog>();

// The tenant's OCR languages, needed by the Repositories detail pane, the Intray staging form and the Tenant
// tab's default. One loader rather than three — see OcrLanguageCatalog.
builder.Services.AddScoped<SimplArchive.Client.Services.OcrLanguageCatalog>();

// The actions a document row offers, shared by the tree pane and the contents list — see DocumentActions.
builder.Services.AddScoped<SimplArchive.Client.Services.DocumentActions>();

// The actions that operate on the multi-selection, plus the runner the drag-drop handler posts through — the
// bulk-bar sibling of DocumentActions (see BulkActions).
builder.Services.AddScoped<SimplArchive.Client.Services.BulkActions>();

// Reading a folder's contents, and describing a node as a tree item / drag participant — shared by the tree
// pane and the contents list, extracted before either of them so neither had to copy it (see BrowseService).
builder.Services.AddScoped<SimplArchive.Client.Services.BrowseService>();

// The caller's addressbooks and calendars, for the Contacts and Calendar tabs (#564). One reader for both:
// the two tabs ask the same endpoint with a different kind, so a client per tab would be one copy to keep in
// step with the other.
builder.Services.AddScoped<SimplArchive.Client.Services.DavCollections>();
builder.Services.AddScoped<SimplArchive.Client.Services.StructuredEditors>();

// The repository tree's nodes. Outside the pane component for the same reason as SearchState: the workbench
// renders one tab at a time, so roots kept in the component would be re-fetched — and every expanded folder
// collapsed — every time the user visits another tab (see TreeState).
builder.Services.AddScoped<SimplArchive.Client.Services.TreeState>();

// What the index-data pane is describing, including an OPEN EDIT's unsaved form. Outside the pane component
// because that component is disposed on a tab switch, and losing a half-filled index form to a glance at
// another tab is exactly the state a user is annoyed to lose (see DetailState).
builder.Services.AddScoped<SimplArchive.Client.Services.DetailState>();

// The four tenant-wide lists the index-data pane offers, and the edit lifecycle that reads them. Scoped for the
// same reason DetailState is: a catalogue fetched once should outlive the pane that asked for it, and the edit
// flag has to survive the tab switch that disposes it.
builder.Services.AddScoped<SimplArchive.Client.Services.IntrayUploads>();
builder.Services.AddScoped<SimplArchive.Client.Services.DetailCatalogs>();
builder.Services.AddScoped<SimplArchive.Client.Services.DetailEditor>();
builder.Services.AddScoped<SimplArchive.Client.Services.DetailLoader>();

// The annotation authoring mode, selection and clipboard (ADRs "Document annotations" / "Annotation
// multi-select"). Scoped for rule 4's reason: the Repositories tab body is disposed on a tab switch, and a
// copied selection is work the user did, not a listing that can simply be re-fetched.
builder.Services.AddScoped<SimplArchive.Client.Services.AnnotationEditor>();

// Outermost, so it sees the FINAL response after the authorization and impersonation handlers (issue #509):
// a 401 means the server repudiated the token, which is app-wide and must send the user to sign in — not be
// reported by whichever tab happened to ask first as its own feature failing.
builder.Services.AddScoped<SimplArchive.Client.Services.SessionExpiredHandler>();

builder.Services.AddHttpClient("SimplArchive.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<SimplArchive.Client.Services.SessionExpiredHandler>())
    .AddHttpMessageHandler(sp => sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<SimplArchive.Client.Services.ImpersonationHandler>());
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("SimplArchive.Api"));

// The same API, without the access-token handler (ADR 0578). Two endpoints are [AllowAnonymous] and are read
// BEFORE anyone signs in: the API root — 36 links, no conditionals, identical for every caller — and the
// installation's theme, which has to be on screen for the sign-in page rather than after it.
//
// With the authorized client those reads throw AccessTokenNotAvailableException while signed out, which is
// swallowed and leaves the shipped design in place: the customer's own colours would appear only after login,
// which is the one moment they are least needed.
builder.Services.AddHttpClient(SimplArchive.Client.Services.ApiRoot.AnonymousClient, client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

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

// Owns what to do when a dropped file collides with an existing name (the dialog + the two outcomes), so the
// workbench page does not (ADR 0558).
builder.Services.AddScoped<SimplArchive.Client.Services.UploadConflictResolver>();
builder.Services.AddScoped<SimplArchive.Client.Services.WebDavMountLink>();

// The UI language is applied at the WASM runtime level via Blazor.start({ applicationCulture }) in index.html
// (ADR "Web UI localization — shared resources") — set before the app runs, so the resx accessor resolves to
// that language on the first render (no live switch; the switcher persists the choice and reloads).
await builder.Build().RunAsync();
