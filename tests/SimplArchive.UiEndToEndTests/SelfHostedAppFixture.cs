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
        // The module offers its reveal-once action on every Test Dossier (the server cannot know a document id yet).
        ExtraEnvironment = new Dictionary<string, string> { ["SIMPLARCHIVE_TESTMODULE_REVEAL_ON_DOSSIERS"] = "1" },
    })
    {
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

