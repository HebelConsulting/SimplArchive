using Microsoft.Playwright;
using SimplArchive.SelfHosting;

namespace SimplArchive.UiEndToEndTests;

// The browser-driven UI E2E fixture: a thin wrapper over the shared SelfHostedApp engine (ADR 0502) that adds one
// system Chrome (channel — no browser download) shared across the collection. The boot logic (Postgres + SeaweedFS
// + OpenSearch + Tika + Gotenberg via Testcontainers, then the real API launched as a subprocess so a real browser
// can reach it, seeded by Demo:*, ADR 0214) lives in SimplArchive.SelfHosting and is shared with the desktop
// fixture + the manual-capture harness — one source of truth, no more hand-synced copies.
public sealed class SelfHostedAppFixture : IAsyncLifetime
{
    private readonly SelfHostedApp _app = new();
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

    public async Task DisposeAsync()
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
