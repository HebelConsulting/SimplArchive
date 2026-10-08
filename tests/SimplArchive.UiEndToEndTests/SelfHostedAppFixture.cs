using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using SimplArchive.SelfHosting;

namespace SimplArchive.UiEndToEndTests;

// The browser-driven UI E2E fixture: a thin wrapper over the shared SelfHostedApp engine (ADR 0502) that adds one
// system Chrome (channel — no browser download) shared across the collection. The boot logic (Postgres + SeaweedFS
// + OpenSearch + Tika + Gotenberg via Testcontainers, then the real API launched as a subprocess so a real browser
// can reach it, seeded by Demo:*, ADR 0214) lives in SimplArchive.SelfHosting and is shared with the desktop
// fixture + the manual-capture harness — one source of truth, no more hand-synced copies.
public class SelfHostedAppFixture : IAsyncLifetime
{
    private readonly SelfHostedApp _app;

    public SelfHostedAppFixture() : this(new SelfHostedApp())
    {
    }

    /// <summary>For a fixture that needs the app started differently (<see cref="ModuleAppFixture"/>).</summary>
    protected SelfHostedAppFixture(SelfHostedApp app) => _app = app;

    /// <summary>The engine, for a derived fixture's helpers.</summary>
    protected SelfHostedApp App => _app;
    private IPlaywright? _playwright;

    public const string AdminEmail = SelfHostedApp.AdminEmail;
    public const string AdminPassword = SelfHostedApp.AdminPassword;
    public const string AdminDisplayName = SelfHostedApp.AdminDisplayName;

    public string BaseUrl => _app.BaseUrl;
    public IBrowser Browser { get; private set; } = null!;

    // The self-hosted app's Postgres — exposed so a test can clean up data it seeded (e.g. removing a passkey from
    // the shared demo admin so it doesn't affect other tests' logins).
    public string PostgresConnectionString => _app.PostgresConnectionString;

    public async Task InitializeAsync()
    {
        await _app.StartAsync();
        _playwright = await Playwright.CreateAsync();
        // Use the system Google Chrome (channel) so no Playwright browser has to be downloaded.
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
    }

    public virtual async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        await _app.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<SelfHostedAppFixture>
{
    public const string Name = "ui-e2e";
}

/// <summary>
/// The app with the TestModule staged, for driving a module's document actions through the browser (#1628). Its own
/// collection, so the shared app keeps running with NO module installed: tests such as the tenant settings' empty
/// modules state depend on that.
/// </summary>
public sealed class ModuleAppFixture : SelfHostedAppFixture
{
    private static readonly System.Security.Cryptography.ECDsa VendorKey =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

    public ModuleAppFixture() : base(new SelfHostedApp
    {
        StageTestModule = true,
        TestModuleVerifyKeyPem = VendorKey.ExportSubjectPublicKeyInfoPem(),
        ExtraEnvironment = new Dictionary<string, string>
        {
            // The module offers its reveal-once action on every Test Dossier (the server cannot know a document id yet).
            ["SIMPLARCHIVE_TESTMODULE_REVEAL_ON_DOSSIERS"] = "1",
            // Everything the host and the app log, as JSON with EVERY property (the scope's RequestPath included), so
            // PathCredentialLogTests reads the real output at its most talkative (ADR 0909). Only the database layer
            // stays quiet: it never sees a request path, and at Verbose it would bury the rest.
            ["Serilog__MinimumLevel__Default"] = "Verbose",
            ["Serilog__MinimumLevel__Override__Microsoft.AspNetCore"] = "Verbose",
            ["Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore"] = "Warning",
            ["Serilog__MinimumLevel__Override__Npgsql"] = "Warning",
            ["Serilog__Using__0"] = "Serilog.Sinks.Console",
            ["Serilog__WriteTo__0__Name"] = "Console",
            ["Serilog__WriteTo__0__Args__formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
        },
    })
    {
    }

    /// <summary>Everything the app has written to its console so far.</summary>
    public string ApiLog() => App.ApiLog();

    /// <summary>
    /// The TestModule activated in the demo tenant, through the API as the demo admin: a repository, the licence filed
    /// in it, activation (ADR 0740). Activating twice in one run is harmless. The caller owns the returned client.
    /// </summary>
    public async Task<(HttpClient Http, Guid TenantId, Guid RepositoryId)> ActivateTestModuleAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(BaseUrl));

        var tenantId = (await http.GetFromJsonAsync<JsonElement>("/api/diagnostics/whoami"))
            .GetProperty("tenantId").GetGuid();
        var repoId = await IdOf(await http.PostAsJsonAsync("/api/repositories", new { name = $"Modules {Guid.NewGuid():N}" }));
        var licenceDoc = await IdOf(await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = "Test module licence" }));
        var version = await (await http.PostAsJsonAsync($"/api/documents/{licenceDoc}/versions", new { fileExtension = ".json" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(SignTestModuleLicence(tenantId))))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{licenceDoc}/versions/{version.GetProperty("id").GetGuid()}", new { }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/api/modules/test-module/license", new { licenseDocumentId = licenceDoc }))
            .EnsureSuccessStatusCode();
        return (http, tenantId, repoId);

        static async Task<Guid> IdOf(HttpResponseMessage response) =>
            (await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>A licence for the TestModule in <paramref name="tenantId"/>, signed with the key the staged module trusts.</summary>
    public string SignTestModuleLicence(Guid tenantId) => System.Text.Json.JsonSerializer.Serialize(
        new SimplArchive.ModuleAbi.TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            SimplArchive.ModuleAbi.ModuleAbiVersion.Major, string.Empty).Sign(VendorKey),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
}

[CollectionDefinition(Name)]
public sealed class ModuleUiCollection : ICollectionFixture<ModuleAppFixture>
{
    public const string Name = "UI with the TestModule";
}

