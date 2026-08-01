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
    private readonly SelfHostedApp _app = new();

    public const string AdminEmail = SelfHostedApp.AdminEmail;
    public const string AdminPassword = SelfHostedApp.AdminPassword;
    public const string AdminDisplayName = SelfHostedApp.AdminDisplayName;

    public string BaseUrl => _app.BaseUrl;

    // The self-hosted app's Postgres — exposed so a test can clean up data it seeded.
    public string PostgresConnectionString => _app.PostgresConnectionString;

    public Task InitializeAsync() => _app.StartAsync();

    public async Task DisposeAsync() => await _app.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<SelfHostedAppFixture>
{
    public const string Name = "ui-e2e";
}
