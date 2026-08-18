using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Fido2NetLib;
using Scalar.AspNetCore;
using Serilog;
using SimplArchive.Api.Authentication;
using SimplArchive.Api.Configuration;
using SimplArchive.Api.Errors;
using SimplArchive.Api.HealthChecks;
using SimplArchive.Api.Logging;
using SimplArchive.Api.Provisioning;
using SimplArchive.Api.Serialization;
using SimplArchive.Api.Versioning;
using SimplArchive.Application.Abstractions;
using SimplArchive.Auth;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.PlatformAdministrators;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Api.Branding;

// Enterprise structured logging (ADR "Enterprise-grade structured logging with Serilog"). A bootstrap logger
// captures anything logged before the host is built; it's replaced by the fully-configured logger (sinks +
// per-source levels) via UseSerilog below. The AppDomain backstop logs a Fatal — service impaired — for any
// otherwise-unhandled exception (e.g. a startup crash) and flushes, so the last words are never lost.
Log.Logger = SerilogConfiguration.CreateBootstrapLogger();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        Log.Fatal(ex, "Unhandled exception — the SimplArchive API is terminating");
    }

    Log.CloseAndFlush();
};

var builder = WebApplication.CreateBuilder(args);

// Route all host + application logging through Serilog, configured from the Serilog config section plus code
// (human-readable console in Development, compact JSON to stdout otherwise).
builder.Host.UseSerilog((context, services, configuration) =>
    SerilogConfiguration.Configure(configuration, context.Configuration, context.HostingEnvironment, services));

// Source secrets from OpenBao before anything reads configuration (ADR "Secrets management with OpenBao"):
// ConnectionStrings:Default (dynamic Postgres cred), ObjectStorage/Smtp/Bootstrap secrets (KV). A no-op when
// OpenBao:Address is unset, so appsettings/env are used as-is (tests, non-OpenBao deployments).
builder.Configuration.AddOpenBaoSecrets();

// Fail-fast production hardening (ADR "Fail-fast production hardening"): outside Development, refuse to start
// with any dev-grade setting (dev OpenIddict certs, plaintext/known-dev credentials, demo seeding, startup
// migration). A no-op in Development, so local dev + the tests are unaffected.
ProductionReadinessValidator.ThrowIfNotProductionReady(builder.Configuration, builder.Environment);

// Add services to the container.

// JSON/XML content negotiation — see ADR "JSON/XML content negotiation". AddXmlSerializerFormatters
// registers the standard XML input/output formatters alongside the default JSON ones; the vendor+version
// media type (application/vnd.simplarchive.v1+json / +xml) is added directly to each formatter's own
// SupportedMediaTypes so ASP.NET Core's built-in negotiation (matching Accept/Content-Type against these
// lists) picks the right formatter without needing any header rewriting. Only v1 exists today; a future
// version would add its own entries here.
builder.Services.AddControllers()
    .AddXmlSerializerFormatters()
    // Every inbound timestamp is normalised to UTC before it reaches a handler: Postgres stores an instant and
    // Npgsql rejects a DateTimeOffset carrying any other offset, so without this a caller in a non-UTC timezone
    // turns a valid request into a 500 deep inside SaveChanges. See UtcDateTimeOffsetConverter.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter()))
    .AddMvcOptions(options =>
    {
        foreach (var formatter in options.OutputFormatters)
        {
            if (formatter is SystemTextJsonOutputFormatter)
            {
                ((TextOutputFormatter)formatter).SupportedMediaTypes.Add("application/vnd.simplarchive.v1+json");
            }
            else if (formatter is XmlSerializerOutputFormatter)
            {
                ((TextOutputFormatter)formatter).SupportedMediaTypes.Add("application/vnd.simplarchive.v1+xml");
            }
        }

        foreach (var formatter in options.InputFormatters)
        {
            if (formatter is SystemTextJsonInputFormatter)
            {
                ((TextInputFormatter)formatter).SupportedMediaTypes.Add("application/vnd.simplarchive.v1+json");
            }
            else if (formatter is XmlSerializerInputFormatter)
            {
                ((TextInputFormatter)formatter).SupportedMediaTypes.Add("application/vnd.simplarchive.v1+xml");
            }
        }
    });

// OpenAPI document generation (ADR "OpenAPI definition endpoint"). Microsoft.AspNetCore.OpenApi (built-in,
// no third-party generator) produces the machine-readable spec at /openapi/v1.json; Scalar renders a
// browsable/try-it UI at /scalar (Development only, mapped below). Minimal auto-generation from the
// controllers/DTOs — no XML doc comments yet.
builder.Services.AddOpenApi();

// Clock (ADR 0510): the app-wide TimeProvider is the real system clock (registered in AddInfrastructure) — auth
// must track real time. A SEPARATE keyed "demo-clock" (also in AddInfrastructure) is a fixed instant only when
// `Demo:Clock` is set by the manual-capture harness, and only the demo seed + audit recorder read it, so the
// manual's time-sensitive screens are byte-stable without freezing the auth clock.
builder.Services.AddInfrastructure(builder.Configuration);

// Persist Data Protection keys in Postgres (ADR 0514) via the EF Core key store, so antiforgery + auth cookies
// survive an API restart and are SHARED across HPA replicas. The default ephemeral per-container key ring
// otherwise regenerates on every restart — breaking the first login after a restart (the browser's antiforgery
// cookie can't be decrypted) and every cookie across replicas. SetApplicationName pins the purpose strings.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>()
    .SetApplicationName("SimplArchive");

builder.Services.AddAuthServer(builder.Configuration, builder.Environment);

// The DAV surfaces authenticate with the SHARED DAV password as a real authentication scheme (ADR 0621), so
// the CalDAV/CardDAV controllers can carry [Authorize] instead of hand-rolling Basic parsing per request.
builder.Services.AddAuthentication()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SimplArchive.Api.CalDav.Authentication.DavBasicAuthenticationHandler>(
        SimplArchive.Api.CalDav.Authentication.DavAuthenticationDefaults.Scheme, _ => { });

// Real-time in-app notifications (ADR "Real-time notifications (SignalR)"): a hub the clients subscribe to, plus
// a hub-context broadcaster overriding the default NullRealtimeNotifier so the DbContext choke point delivers
// live. SubjectUserIdProvider keys connections to the User id.
var signalR = builder.Services.AddSignalR();

// Valkey/Redis backplane (ADR "SignalR Valkey backplane") — when ConnectionStrings:Valkey is set, a push on any
// API replica fans out to clients connected to every replica (needed once the Helm HPA scales past one pod).
// Unset ⇒ in-process only (single-replica deployments + tests are unchanged). A channel prefix isolates this
// app's messages on a shared Valkey.
var valkeyConnection = builder.Configuration.GetConnectionString("Valkey");
if (!string.IsNullOrWhiteSpace(valkeyConnection))
{
    signalR.AddStackExchangeRedis(valkeyConnection, options =>
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("simplarchive-signalr"));
}

builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SimplArchive.Api.Realtime.SubjectUserIdProvider>();
builder.Services.AddSingleton<SimplArchive.Application.Abstractions.IRealtimeNotifier, SimplArchive.Api.Realtime.SignalRRealtimeNotifier>();

// The IMAP endpoint (ADR "IMAP endpoint (read-only, first slice)", #562) — a raw TCP hosted service, off
// unless Imap:Enabled. Registered as a singleton FIRST so tests (and the dialog surface) can read the bound
// ports back from the same instance the host runs.
builder.Services.Configure<SimplArchive.Api.Imap.ImapOptions>(builder.Configuration.GetSection(SimplArchive.Api.Imap.ImapOptions.SectionName));
builder.Services.AddSingleton<SimplArchive.Api.Imap.ImapServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimplArchive.Api.Imap.ImapServer>());

// The LMTP delivery listener (ADR 0628). Off unless configured: an installation with no MTA in front of it
// must not open a listener that accepts mail without authentication.
builder.Services.Configure<SimplArchive.Api.Lmtp.LmtpOptions>(builder.Configuration.GetSection("Lmtp"));
builder.Services.AddScoped<SimplArchive.Api.Lmtp.LmtpDelivery>();
builder.Services.AddSingleton<SimplArchive.Api.Lmtp.LmtpServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimplArchive.Api.Lmtp.LmtpServer>());

// WebAuthn / passkeys (ADR "WebAuthn passkeys as a second factor"). The Relying Party id is the registrable
// domain (host of App:BaseUrl, e.g. "localhost"); the expected origin is the full base URL. Fido2NetLib (MIT).
var webAuthnBaseUrl = builder.Configuration["App:BaseUrl"];
if (!string.IsNullOrWhiteSpace(webAuthnBaseUrl))
{
    var origin = new Uri(webAuthnBaseUrl);
    builder.Services.AddFido2(options =>
    {
        options.ServerDomain = origin.Host;
        options.ServerName = "SimplArchive";
        options.Origins = new HashSet<string> { origin.GetLeftPart(UriPartial.Authority) };
    });
}

// Shared tenant provisioning (Tenant + masks + TenantAdministrator + first repository + full-rights ACL),
// used by both TenantsController and the Compose demo-data seeder below — see ADR "Compose demo-data
// seeding".
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

// Confirms + auto-classifies an uploaded/filed DocumentVersion — shared by version finalize and intray
// filing (ADR "S3-backed inbox").
// WebDAV-Push (#564 slice 3, ADR 0622): the VAPID configuration is a singleton because in Development it
// GENERATES an ephemeral key pair — one per process, not one per request, or every client's registration
// would be signed by a different key.
builder.Services.Configure<SimplArchive.Api.CalDav.DavPushOptions>(
    builder.Configuration.GetSection(SimplArchive.Api.CalDav.DavPushOptions.SectionName));
builder.Services.AddSingleton<SimplArchive.Api.CalDav.DavPushConfiguration>();
builder.Services.AddScoped<SimplArchive.Api.CalDav.DavPushNotifier>();
builder.Services.AddScoped<SimplArchive.Api.Documents.CalendarContactClassifier>();
// Composing a note as the .eml a notes client expects (#564) — the workbench's "New note" meets the IMAP
// write path at one shape, written down in one place.
builder.Services.AddScoped<SimplArchive.Api.Documents.NoteComposer>();
builder.Services.AddScoped<SimplArchive.Api.Documents.DocumentFinalizer>();
builder.Services.AddScoped<SimplArchive.Api.Documents.ChatSystemEntryRecorder>();

// Permanent purge of recycle-bin documents (blobs + rows + search index) — shared by DocumentsController
// (per-item) and RepositoriesController (empty recycle bin). See ADR "Manual hard-delete / purge".
builder.Services.AddScoped<SimplArchive.Api.Documents.DocumentPurger>();
// The caller-facing access questions every Document-scope controller asks (issue #466) — one implementation
// where each controller used to carry its own copy of GetCallerRightsAsync.
builder.Services.AddScoped<SimplArchive.Api.Documents.DocumentAccessService>();
// Restore of recycle-bin documents — shared by DocumentsController (per-item) and RecycleBinController (bulk).
// See ADR "Bulk restore from the recycle bin".
builder.Services.AddScoped<SimplArchive.Api.Documents.DocumentRestorer>();
// The intray's page operations and the scope/authorization rule they share with IntrayController (ADR 0575).
builder.Services.AddScoped<SimplArchive.Api.Intray.IntrayScopeResolver>();
builder.Services.AddScoped<SimplArchive.Api.Intray.IntrayPageService>();
builder.Services.AddScoped<SimplArchive.Api.Checkouts.CheckoutPageService>();
builder.Services.AddScoped<SimplArchive.Api.Documents.RepositoryExporter>();
builder.Services.AddScoped<SimplArchive.Api.Documents.RepositoryImporter>();
// Get-or-create the caller's personal repository — shared by PersonalRepositoryController and the WebDAV
// gateway (which nests Intray / Check-out under Personal). See ADR "WebDAV Inbox/Check-out under Personal".
builder.Services.AddScoped<SimplArchive.Api.Documents.PersonalRepositoryProvisioner>();
// Bulk clearance filtering for listings + search (ADR "Sensitivity clearance enforcement"); the per-document
// CanSee authority is IEffectiveRightsCalculator.
builder.Services.AddScoped<SimplArchive.Api.Documents.IClearanceScopeResolver, SimplArchive.Api.Documents.ClearanceScopeResolver>();
// In-memory WebDAV lock store (ADR "WebDAV hardening") — a singleton so locks live across requests.
builder.Services.AddSingleton<SimplArchive.Api.WebDav.WebDavLockStore>();

// TOTP two-factor helpers (secret/QR/verify/recovery codes) — shared by the login page and UsersController
// (ADR "MFA (interactive login, TOTP)"). Stateless singleton.
builder.Services.AddSingleton<SimplArchive.Api.Authentication.MfaService>();

// The server-rendered login page (Pages/Account/Login.cshtml) — see ADR "Interactive User login
// (foundation slice)". A Blazor WASM SPA can't easily be the redirect target OpenIddict's
// Authorization Code flow needs, so this is a separate, minimal Razor Pages surface.
builder.Services.AddRazorPages();

// Backs the browsable /download desktop-client area (ADR 0490).
builder.Services.AddDirectoryBrowser();

// /health/live (no checks — just proves the process can respond) and /health/ready (database
// connectivity, the one dependency every request actually needs) — see ADR "Health check endpoints".
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// RFC 7807 Problem Details for unhandled exceptions and model-validation failures — see ADR "Hypermedia
// envelope and Problem Details errors (foundation slice)". CustomizeProblemDetails also covers
// ProblemDetails written directly by other middleware sharing this same service (e.g. Asp.Versioning's
// own "unsupported API version" response) — remapping its "code" extension to our own "errorCode"
// convention rather than leaving two different error-shape conventions in the same Api. See ADR
// "Media-type/Accept-header API versioning (foundation slice)".
// Rate limiting (ADR 0546) — the first in this codebase, scoped to the one anonymous endpoint. A public route
// whose token IS its credential is exactly what token-guessing and denial of service target, and a limiter is far
// easier to add alongside the endpoint than to retrofit once it is live.
//
// Keyed by client IP: there is no principal to key on, which is the whole point of the endpoint. A legitimate
// recipient opens a link a handful of times; anyone probing for valid tokens needs orders of magnitude more.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(SimplArchive.Api.RateLimitPolicies.ExternalLinks, context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, // reject immediately rather than queue — a prober should feel the wall at once
            }));
});

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("errorCode")
            && context.ProblemDetails.Extensions.TryGetValue("code", out var code))
        {
            context.ProblemDetails.Extensions["errorCode"] = code switch
            {
                "UnsupportedApiVersion" => "UNSUPPORTED_API_VERSION",
                _ => code,
            };
            context.ProblemDetails.Extensions.Remove("code");
        }
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Media-type/Accept-header API versioning — see ADR "Media-type/Accept-header API versioning
// (foundation slice)". A request with no recognized versioned media type implicitly gets the current
// (default) version rather than being rejected. DefaultApiVersion is parsed via ApiVersionParser.Default
// (matching ADR 0012's "v1" example, major-only) rather than constructed as new ApiVersion(1, 0) — the
// two produce different ToString() formats ("1" vs "1.0"), which would make the negotiated Content-Type
// inconsistent between an explicit "v1" request and an unspecified one that falls back to this default.
ApiVersionParser.Default.TryParse("1", out var defaultApiVersion);
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = defaultApiVersion!;
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ApiVersionReader = new VendorMediaTypeApiVersionReader();
    options.ReportApiVersions = true;
}).AddMvc();

var app = builder.Build();

// Applies migrations through the dedicated owner connection when one is provisioned (ADR "Dedicated migration
// owner role") — the running app's OpenBao dynamic role has DML grants but can't run DDL (it isn't the table
// owner), so migrations use a separate owner identity. Falls back to the Default connection when no owner is
// configured (tests / non-OpenBao deployments), leaving that path unchanged.
async Task ApplyMigrationsAsync(IServiceProvider services)
{
    var migrationConnection = app.Configuration.GetConnectionString("Migration");
    if (!string.IsNullOrWhiteSpace(migrationConnection))
    {
        await DatabaseMigrator.MigrateAsync(migrationConnection);
    }
    else
    {
        await services.GetRequiredService<SimplArchiveDbContext>().Database.MigrateAsync();
    }
}

// Migrate-and-exit mode (ADR "Data-preserving migrations"): `--migrate` applies EF Core migrations then exits,
// so production runs migrations as a one-off step (a Helm pre-upgrade Job) rather than at app startup — which
// races across replicas and is refused by the production-readiness validator. Uses the same MigrateAsync path.
if (args.Contains("--migrate"))
{
    using var migrateScope = app.Services.CreateScope();
    await ApplyMigrationsAsync(migrateScope.ServiceProvider);
    return;
}

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    // Config-gated automatic migration — off by default (existing manual/test workflows are unchanged), on
    // in the Docker Compose stack so `docker compose up` works against a fresh database with no manual
    // `dotnet-ef database update` step. See ADR "Local development Docker Compose stack".
    if (app.Configuration.GetValue<bool>("App:ApplyMigrationsAtStartup"))
    {
        await ApplyMigrationsAsync(services);
    }

    // Idempotent well-known-mask backfill for every EXISTING tenant — a newly added well-known mask (the
    // NoteFolder/Note pair, #562 slice 5, was the first since launch) is otherwise seeded only at tenant
    // provisioning, so tenants created before an upgrade silently miss it; the demo stack's Personal/Notes
    // folder came out maskless because of exactly that. WellKnownMaskSeeder checks each mask individually,
    // so this is a handful of cheap existence probes per tenant per startup.
    {
        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();
        var maskSeeder = services.GetRequiredService<IWellKnownMaskSeeder>();
        foreach (var tenantId in await dbContext.Tenants.Select(t => t.Id).ToListAsync())
        {
            await maskSeeder.EnsureWellKnownMasksAsync(tenantId);
        }
    }

    var applicationManager = services.GetRequiredService<IOpenIddictApplicationManager>();

    // Idempotently seeds the Blazor Client's OpenIddict application — a fixed, one-per-deployment public
    // client (no secret to protect; unlike ServiceAccount/PlatformAdministrator clients, which are created
    // dynamically through the Api with their own secrets), so the Client works out of the box with no
    // manual setup step. Mirrors IWellKnownMaskSeeder's idempotent-seed-if-missing pattern. See ADR "Blazor
    // Client-side login wiring".
    {
        var baseUrl = (app.Configuration["App:BaseUrl"]
            ?? throw new InvalidOperationException("Missing required 'App:BaseUrl' configuration value.")).TrimEnd('/');

        // Every origin the app can be reached from needs its OIDC redirect URIs registered — OpenIddict
        // validates redirect_uri exactly. Besides App:BaseUrl (local), App:ProxyBaseUrl is the LAN reverse
        // proxy (ADR "Reverse proxy for LAN testing"), so devices browsing https://<host>:9443 can log in.
        var origins = new List<string> { baseUrl };
        var proxyBaseUrl = app.Configuration["App:ProxyBaseUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(proxyBaseUrl) && !origins.Contains(proxyBaseUrl, StringComparer.OrdinalIgnoreCase))
        {
            origins.Add(proxyBaseUrl);
        }

        var existing = await applicationManager.FindByClientIdAsync("blazor-client");
        if (existing is null)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = "blazor-client",
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Scopes.Email,
                    OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                    // RFC 8693 token exchange for User impersonation (ADR "User impersonation").
                    OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType,
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
                },
            };
            foreach (var origin in origins)
            {
                descriptor.RedirectUris.Add(new Uri($"{origin}/authentication/login-callback"));
                descriptor.PostLogoutRedirectUris.Add(new Uri($"{origin}/authentication/logout-callback"));
            }

            await applicationManager.CreateAsync(descriptor);
        }
        else
        {
            // Ensure every configured origin's redirect URIs are present (e.g. a proxy origin added after the
            // app was first seeded), updating in place if any are missing.
            var descriptor = new OpenIddictApplicationDescriptor();
            await applicationManager.PopulateAsync(descriptor, existing);

            var changed = false;
            foreach (var origin in origins)
            {
                var login = new Uri($"{origin}/authentication/login-callback");
                var logout = new Uri($"{origin}/authentication/logout-callback");
                if (!descriptor.RedirectUris.Contains(login)) { descriptor.RedirectUris.Add(login); changed = true; }
                if (!descriptor.PostLogoutRedirectUris.Contains(logout)) { descriptor.PostLogoutRedirectUris.Add(logout); changed = true; }
            }

            if (changed)
            {
                await applicationManager.UpdateAsync(existing, descriptor);
            }
        }
    }

    // Idempotently seeds the cross-platform desktop fat client's OpenIddict application — a public client
    // using Authorization Code + PKCE with a fixed loopback redirect (RFC 8252 "OAuth for Native Apps"). See
    // ADR "Cross-platform desktop fat client (Avalonia)".
    if (await applicationManager.FindByClientIdAsync("simplarchive-desktop") is null)
    {
        await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = "simplarchive-desktop",
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            RedirectUris = { new Uri("http://127.0.0.1:8765/callback") },
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                // RFC 8693 token exchange for User impersonation (ADR "User impersonation").
                OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            },
        });
    }

    // Env-driven idempotent bootstrap of the first PlatformAdministrator — the deployment-level chicken/egg
    // (a PlatformAdministrator can only be created by another PlatformAdministrator) needs one seeded out of
    // band. Runs only when a bootstrap client id/secret is configured AND no active PlatformAdministrator
    // already exists, so it's a safe no-op on every restart and in every non-configured environment. The
    // Docker Compose stack supplies the credentials via env; production seeds its first admin the same
    // deployment-level way (like OpenIddict's dev certificates). See ADR "Local development Docker Compose
    // stack" and ADR "Tenant onboarding and platform-admin mechanism".
    var bootstrapClientId = app.Configuration["Bootstrap:PlatformAdministrator:ClientId"];
    var bootstrapClientSecret = app.Configuration["Bootstrap:PlatformAdministrator:ClientSecret"];

    if (!string.IsNullOrWhiteSpace(bootstrapClientId) && !string.IsNullOrWhiteSpace(bootstrapClientSecret))
    {
        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();

        if (!await dbContext.PlatformAdministrators.AnyAsync(p => p.IsActive))
        {
            if (await applicationManager.FindByClientIdAsync(bootstrapClientId) is null)
            {
                await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
                {
                    ClientId = bootstrapClientId,
                    ClientSecret = bootstrapClientSecret,
                    Permissions =
                    {
                        OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                    },
                });
            }

            dbContext.PlatformAdministrators.Add(new PlatformAdministrator
            {
                Id = Guid.NewGuid(),
                Name = app.Configuration["Bootstrap:PlatformAdministrator:Name"] ?? "bootstrap-admin",
                OpenIddictApplicationClientId = bootstrapClientId,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await dbContext.SaveChangesAsync();
        }
    }

    // Env-driven idempotent demo-data seed (Docker Compose / kiosk only) — provisions a demo tenant + admin
    // with a KNOWN password and a realistic sample tree (Business Years / Contracts / General, varied file types,
    // references, two extra users + a shared group intray), so a visitor can log straight in and see content
    // (ADR "Compose demo-data seeding" / 0214; issue #354). No-op unless the Demo:* config is present and the
    // tenant doesn't already exist. The seeding logic lives in DemoDataSeeder (extracted from this file).
    await DemoDataSeeder.SeedIfConfiguredAsync(services, app.Configuration);

    // Env-driven idempotent seed of a second, deliberately EMPTY tenant + its machine principal, as the target for
    // external-system migration runs (ADR "A seeded migration-target tenant"). No-op unless the Interop:* config
    // is present and the tenant doesn't already exist. Its point is that the service-account credentials come from
    // config instead of being minted per stack: a secret is shown once and stored hashed, so recreating the
    // volumes used to invalidate the tooling's saved credentials and every run died at `invalid_client`.
    await InteropTenantSeeder.SeedIfConfiguredAsync(services, app.Configuration);
}

// Configure the HTTP request pipeline.

// Behind a TLS-terminating reverse proxy (the caddy service for LAN testing — ADR "Reverse proxy for LAN
// testing"), honor X-Forwarded-Proto/-Host/-For so OpenIddict + link generation emit the external https URLs
// the browser actually used, not the internal http ones. Gated on App:TrustProxyHeaders (default off, so
// direct/local access + the test suite are unchanged); dev-only "trust any proxy" since the Api is only ever
// reached through the proxy in that setup. Must run first so downstream sees the corrected scheme/host.
if (app.Configuration.GetValue<bool>("App:TrustProxyHeaders"))
{
    var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
    };
    forwardedOptions.KnownIPNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
}

app.UseExceptionHandler();

// Request localization for the server-rendered pages (the /Account/Login OAuth surface, ADR "Server login-page
// localization"): the culture is selected from the browser's Accept-Language header (the SPA + desktop apply their
// own in-app language client-side, so this only governs the login round-trip). Supported: en (default)/de/it/es —
// resolves SimplArchive.Localization.Strings for the request via CurrentUICulture.
var supportedCultures = new[] { "en", "de", "it", "es" };
app.UseRequestLocalization(new Microsoft.AspNetCore.Builder.RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures));

// Correlation id on every log line for the request (ADR "Enterprise-grade structured logging with Serilog"),
// established ahead of auth so even auth/OpenIddict logs are correlated.
app.UseMiddleware<CorrelationIdMiddleware>();

// One Information summary per request — "HTTP {Method} {Path} responded {StatusCode} in {Elapsed}ms" — enriched
// with the resolved tenant/principal (available by the time the completion event is written). A failed request
// (unhandled exception) is logged at Error by Serilog's request logging.
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnostic, httpContext) =>
    {
        var services = httpContext.RequestServices;
        if (services.GetService<ICurrentTenantAccessor>()?.TenantId is { } tenantId) diagnostic.Set("TenantId", tenantId);
        if (services.GetService<ICurrentUserAccessor>()?.UserId is { } userId) diagnostic.Set("UserId", userId);
        if (services.GetService<ICurrentServiceAccountAccessor>()?.ServiceAccountId is { } saId) diagnostic.Set("ServiceAccountId", saId);
    };
});

// The WebDAV gateway (ADRs "WebDAV gateway" / 0509) handles /SimplArchive (and the /webdav alias) with its own
// HTTP Basic auth, ahead of the normal OIDC/JWT pipeline; it short-circuits for those and passes the rest through.
app.UseMiddleware<SimplArchive.Api.WebDav.WebDavMiddleware>();

// The CalDAV/CardDAV gateway (#564, ADR 0619) handles /caldav + /carddav and the two well-known discovery
// URIs, with the same HTTP Basic auth against the SHARED DAV password; it passes everything else through.
// DAV observability (ported from SimplCalCon, ADR 0621) — a pass-through unless the SimplArchive.Dav.Wire
// category is at Trace, except for one Warning it always emits: a DAV request that fell through unhandled,
// which is what a native-client compatibility gap looks like from the server side.
app.UseMiddleware<SimplArchive.Api.CalDav.DavWireTraceMiddleware>();

app.UseBlazorFrameworkFiles();

// An installation's own tab icon, if it dropped one beside its theme (ADR 0578). BEFORE UseStaticFiles,
// because static files serve the first match and the shipped icon is already in wwwroot.
app.UseCustomFavicon();

// llms.txt and the tour script are UTF-8 prose read by browsers and agents; without an explicit charset the
// default text/plain and text/markdown mappings let the client guess, and the guess renders em-dashes as
// mojibake (found by actually performing the tour, #530-adjacent). Everything else keeps the stock mapping.
var staticContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".txt"] = "text/plain; charset=utf-8";
staticContentTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });

// Public desktop-client download area (ADR 0490): make ONLY /download browsable so a visitor can click through to
// clients/<os>/ and grab the build that matches this API. The win/linux archives are baked into the image by the
// Dockerfile; clients/macos/ carries an index.html linking to the GitHub Release (a Linux image can't build the
// .dmg). Placed BEFORE UseAuthentication/UseAuthorization, so the listing + downloads are anonymous. Uses a real
// PhysicalFileProvider (UseDirectoryBrowser needs directory enumeration, which the static-web-assets manifest
// provider used under `dotnet run` doesn't support) — resolved across the published (wwwroot) + dev layouts. The
// dedicated static-file handler sets ServeUnknownFileTypes so .dmg/.tar.gz download rather than 404 on the default
// (known-types-only) middleware above.
var downloadDir = new[]
{
    string.IsNullOrEmpty(app.Environment.WebRootPath) ? null : System.IO.Path.Combine(app.Environment.WebRootPath, "download"),
    System.IO.Path.Combine(app.Environment.ContentRootPath, "wwwroot", "download"),
    System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "download"),
    // dev: `dotnet run` sets the content root to the repo root, so the API's own wwwroot is under src/.
    System.IO.Path.Combine(app.Environment.ContentRootPath, "src", "SimplArchive.Api", "wwwroot", "download"),
}.FirstOrDefault(p => p is not null && System.IO.Directory.Exists(p));

if (downloadDir is not null)
{
    var downloadProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(downloadDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = downloadProvider, RequestPath = "/download" });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = downloadProvider,
        RequestPath = "/download",
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream",
    });
    app.UseDirectoryBrowser(new DirectoryBrowserOptions
    {
        FileProvider = downloadProvider,
        RequestPath = "/download",
        // The product's own design rather than the framework's "Index of /download/…" (ADR 0578). The
        // listing stays dynamic because archive filenames carry a version that changes every release.
        Formatter = new SimplArchive.Api.Download.ThemedDirectoryFormatter(),
    });
}

// Explicit UseRouting so it runs HERE, after the /download static/browse middleware — not auto-injected at the
// pipeline start. Otherwise the SPA fallback endpoint ({*path}) is already matched by the time the directory
// browser runs, and the static-file family no-ops when an endpoint is selected (so /download/ would fall to the
// SPA instead of listing).
app.UseRouting();

// SignalR hub handshake (ADR "Real-time notifications (SignalR)"): a browser WebSocket can't set the
// Authorization header, so the access token arrives as ?access_token=. Copy it into the header for /hubs/* before
// authentication runs, so the standard OpenIddict validation picks it up (transport-agnostic — no OpenIddict/
// JwtBearer event wiring needed).
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hubs")
        && string.IsNullOrEmpty(context.Request.Headers.Authorization)
        && context.Request.Query.TryGetValue("access_token", out var token)
        && !string.IsNullOrEmpty(token))
    {
        context.Request.Headers.Authorization = $"Bearer {token}";
    }

    await next();
});

// Before authentication: an anonymous endpoint's limiter must apply to callers who never authenticate at all.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CurrentPrincipalMiddleware>();
// Once the caller is resolved, stamp tenant/principal onto every downstream log line (ADR "Enterprise-grade
// structured logging with Serilog").
app.UseMiddleware<PrincipalLogContextMiddleware>();
app.UseMiddleware<VersionedContentTypeMiddleware>();

app.MapControllers();
app.MapRazorPages();
app.MapHub<SimplArchive.Api.Realtime.NotificationsHub>("/hubs/notifications");

// Language selector on the server-rendered /Account/Login page (ADR 0515): set the culture cookie the
// RequestLocalization CookieRequestCultureProvider reads, then return to the login page. Anonymous — it runs
// before sign-in and only changes the display language, so a GET link (no antiforgery) is appropriate. The
// return target is validated as a local URL to avoid an open redirect.
app.MapGet("/Account/SetLanguage", (HttpContext http, string? culture, string? returnUrl) =>
{
    string[] supported = ["en", "de", "it", "es"];
    var chosen = supported.Contains(culture) ? culture! : "en";
    http.Response.Cookies.Append(
        Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName,
        Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.MakeCookieValue(
            new Microsoft.AspNetCore.Localization.RequestCulture(chosen)),
        new CookieOptions { Path = "/", Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });

    var target = "/Account/Login";
    if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
    {
        target = $"{target}?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
    }
    return Results.LocalRedirect(target);
}).AllowAnonymous();

// No authentication — matches every other infrastructure-level route (GET /, /connect/token,
// /.well-known/openid-configuration) rather than the resource controllers. Registered before
// MapFallbackToFile purely for readability; explicit routes always take precedence over a fallback route
// regardless of registration order.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteResponse,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteResponse,
});

// OpenAPI (ADR "OpenAPI definition endpoint"). The JSON document is served in every environment, anonymous —
// consistent with the /api discovery document and /health endpoints (the API shape isn't secret, and it must
// be reachable by importers/codegen). The interactive Scalar UI is Development-only, keeping a browsable
// playground out of production. /openapi is not under /api, so VersionedContentTypeMiddleware leaves its
// standard application/json content type alone (importers require it).
app.MapOpenApi().AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference(options => options.WithTitle("SimplArchive API"));
}

app.MapFallbackToFile("index.html");

// Lifecycle logging (ADR "Enterprise-grade structured logging with Serilog") + a graceful final flush so
// buffered events are written on a clean shutdown.
app.Lifetime.ApplicationStarted.Register(() =>
    Log.Information("SimplArchive API started in the {Environment} environment", app.Environment.EnvironmentName));
app.Lifetime.ApplicationStopping.Register(() => Log.Information("SimplArchive API is shutting down"));
app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

app.Run();

// Exposed so the end-to-end tests can host the real app in-process via WebApplicationFactory<Program> (ADR
// "Container-backed end-to-end integration tests"). Program is otherwise an internal top-level-statements class.
public partial class Program;
