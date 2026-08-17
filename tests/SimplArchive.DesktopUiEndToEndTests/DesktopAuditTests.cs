using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the audit viewer (ADR "Desktop audit viewer"): the real DesktopClient
// SimplArchiveApiClient drives list / filter / verify / retention get-set / purge against the running API.
// Verifies the desktop api-client wiring end to end (the XAML/VM is exercised by the --audit headless
// screenshot). The demo admin is a tenant admin holding CanViewAuditLog, so all paths are permitted.
[Collection(UiCollection.Name)]
public class DesktopAuditTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAuditTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task List_filter_verify_retention_and_purge()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // whoami exposes CanViewAuditLog — this gates the desktop Audit tab's visibility.
        Assert.True((await client.GetWhoAmIAsync()).CanViewAuditLog);

        // The interactive login recorded an Auth.LoggedIn event, so the log is non-empty.
        var page = await client.Audit.GetAuditEventsAsync(null, null, null, null);
        Assert.NotEmpty(page.Events);

        // The action filter narrows to exactly that action.
        var filtered = await client.Audit.GetAuditEventsAsync("Auth.LoggedIn", null, null, null);
        Assert.NotEmpty(filtered.Events);
        Assert.All(filtered.Events, e => Assert.Equal("Auth.LoggedIn", e.Action));

        // The tamper-evidence chain verifies clean.
        var verify = await client.Audit.VerifyAuditChainAsync();
        Assert.True(verify.Valid);
        Assert.True(verify.CheckedCount > 0);

        // NDJSON export — one event per line, each with the chain fields.
        var exportBytes = await client.Audit.ExportAuditEventsAsync(null, null, null);
        var lines = System.Text.Encoding.UTF8.GetString(exportBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, l =>
        {
            var e = System.Text.Json.JsonDocument.Parse(l).RootElement;
            Assert.False(string.IsNullOrEmpty(e.GetProperty("hash").GetString()));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("action").GetString()));
        });

        // Retention get/set (the demo admin is a tenant admin) — round-trip a value then restore the default.
        Assert.Equal(365, (await client.Audit.GetAuditRetentionAsync()).RetentionDays);
        Assert.Equal(400, (await client.Audit.SetAuditRetentionAsync(400)).RetentionDays);
        Assert.Equal(365, (await client.Audit.SetAuditRetentionAsync(365)).RetentionDays);

        // Purge is non-destructive here (all events are fresh; nothing is older than the window).
        Assert.Equal(0, (await client.Audit.PurgeAuditAsync()).PurgedCount);
    }
}
