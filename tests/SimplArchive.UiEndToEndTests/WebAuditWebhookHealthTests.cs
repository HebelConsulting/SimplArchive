using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Tenant tab's read-only webhook-delivery health line (ADR "Audit webhook delivery retry/backoff"): with a
// failing webhook seeded on the tenant (a future next-attempt so the background worker leaves it alone during the
// test), the Tenant tab shows the "Delivery: failing" status with the consecutive-failure count + last error.
// Cleans the webhook columns off the shared demo tenant afterwards.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebAuditWebhookHealthTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAuditWebhookHealthTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tenant_tab_shows_webhook_delivery_health_when_failing()
    {
        var page = await Ui.LoginAsync(_app);
        try
        {
            await using (var conn = new NpgsqlConnection(_app.PostgresConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "UPDATE \"Tenants\" SET \"AuditWebhookUrl\" = 'https://siem.invalid/ingest', \"AuditWebhookSecret\" = 'seed', " +
                    "\"AuditWebhookConsecutiveFailures\" = 3, \"AuditWebhookLastError\" = 'HTTP 503', " +
                    "\"AuditWebhookLastFailureAt\" = now(), \"AuditWebhookNextAttemptAt\" = now() + interval '1 hour';", conn);
                await cmd.ExecuteNonQueryAsync();
            }

            await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
            var view = page.Locator(".wb-tenant");
            await Expect(view).ToBeVisibleAsync();

            await Expect(view.GetByText(new System.Text.RegularExpressions.Regex("Delivery: failing"))).ToBeVisibleAsync();
            await Expect(view.GetByText(new System.Text.RegularExpressions.Regex("3 consecutive failures"))).ToBeVisibleAsync();
            await Expect(view.GetByText(new System.Text.RegularExpressions.Regex("HTTP 503"))).ToBeVisibleAsync();

            // The "Send test event" button (ADR "Audit webhook test delivery") is present (a webhook is configured)
            // and reports the delivery outcome — here a failure, since the seeded URL is unreachable.
            await view.GetByRole(AriaRole.Button, new() { Name = "Send test event" }).ClickAsync();
            await Expect(page.GetByText(new System.Text.RegularExpressions.Regex("Test delivery failed"))).ToBeVisibleAsync(new() { Timeout = 15000 });
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_app.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE \"Tenants\" SET \"AuditWebhookUrl\" = NULL, \"AuditWebhookSecret\" = NULL, \"AuditWebhookConsecutiveFailures\" = 0, " +
                "\"AuditWebhookLastError\" = NULL, \"AuditWebhookLastFailureAt\" = NULL, \"AuditWebhookNextAttemptAt\" = NULL, " +
                "\"AuditWebhookLastSuccessAt\" = NULL, \"AuditWebhookDeliveredThrough\" = -1;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
