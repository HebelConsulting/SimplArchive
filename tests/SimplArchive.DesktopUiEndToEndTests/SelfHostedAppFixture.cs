using SimplArchive.SelfHosting;

namespace SimplArchive.UiEndToEndTests;

// The desktop-client E2E fixture: a thin wrapper over the shared SelfHostedApp engine (ADR 0502). The desktop
// SimplArchiveApiClient drives the real API over HTTP exactly as the shipped desktop app does, so — unlike the web
// fixture — this one adds **no Playwright/Chrome** (the desktop tests never open a browser), keeping the desktop
// suite light and its own parallel CI job (ADR 0378). The boot logic (Postgres + SeaweedFS + OpenSearch + Tika +
// Gotenberg via Testcontainers, then the real API as a subprocess, seeded by Demo:*, ADR 0214) lives once in
// SimplArchive.SelfHosting, shared with the web fixture + the manual-capture harness — no more hand-synced copies.
public sealed class SelfHostedAppFixture : IAsyncLifetime
{
    /// <summary>The tenant this fixture declares STRICT (core #1421) — created by whichever test wants it.</summary>
    /// <remarks>
    /// Declared here because a MODE is read from configuration at startup, so it cannot be switched on later;
    /// the tenant is a row and belongs to the test. Same division the E2E factory draws.
    /// <para>
    /// This is the desktop suite's one opt-in to an encrypting installation. It sets `Encryption:ServiceUrl`
    /// for the whole app, which swaps the object-storage client for its encrypting decorator — but a MODE is
    /// per tenant (ADR 0825), so the Demo tenant every other desktop test uses is untouched and objects
    /// without the wrapping metadata pass straight through. The E2E factory has done exactly this for its
    /// whole collection since #1387.
    /// </para>
    /// </remarks>
    public const string StrictTenantName = "DesktopStrict";

    private const string PlatformAdminClientId = "desktop-e2e-platform-admin";
    private const string PlatformAdminClientSecret = "desktop-e2e-platform-secret";

    // The vendor key for the staged TestModule (ADR 0890): generated per run, its public half handed to the Api,
    // its private half signing the Strict tenant's licence — so activation verifies for real, with no key in the repo.
    private readonly System.Security.Cryptography.ECDsa _testModuleVendorKey =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

    private readonly SelfHostedApp _app;

    public SelfHostedAppFixture() => _app = new SelfHostedApp
    {
        StrictTenantName = StrictTenantName,
        PlatformAdminClientId = PlatformAdminClientId,
        PlatformAdminClientSecret = PlatformAdminClientSecret,
        TestModuleVerifyKeyPem = _testModuleVendorKey.ExportSubjectPublicKeyInfoPem(),
    };

    /// <summary>A licence for the TestModule, signed for <paramref name="tenantId"/> — what the tenant files to activate it.</summary>
    public string SignTestModuleLicence(Guid tenantId) => System.Text.Json.JsonSerializer.Serialize(
        new SimplArchive.ModuleAbi.TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            SimplArchive.ModuleAbi.ModuleAbiVersion.Major, string.Empty).Sign(_testModuleVendorKey),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    /// <summary>The stub encryption service, so a test can provision a reader's certificate as the real one would.</summary>
    public EncryptionServiceStub EncryptionService => _app.EncryptionService
        ?? throw new InvalidOperationException("the encryption stub is not running — StrictTenantName was not set");

    /// <summary>Client credentials for the bootstrapped platform administrator, which provisions a tenant.</summary>
    public (string ClientId, string ClientSecret) PlatformAdmin => (PlatformAdminClientId, PlatformAdminClientSecret);

    public const string AdminEmail = SelfHostedApp.AdminEmail;
    public const string AdminPassword = SelfHostedApp.AdminPassword;
    public const string AdminDisplayName = SelfHostedApp.AdminDisplayName;

    public string BaseUrl => _app.BaseUrl;

    // The self-hosted app's Postgres — exposed so a test can clean up data it seeded.
    public string PostgresConnectionString => _app.PostgresConnectionString;

    /// <summary>The Api subprocess's log so far — for tests diagnosing a server-side answer (see SelfHostedApp.ApiLog).</summary>
    public string ApiLog() => _app.ApiLog();

    public Task InitializeAsync() => _app.StartAsync();

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _testModuleVendorKey.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<SelfHostedAppFixture>
{
    public const string Name = "ui-e2e";
}
