using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres: the audit-webhook test-delivery action (ADR "Audit webhook test
// delivery") POSTs a synthetic, HMAC-signed audit event to the tenant's saved SIEM webhook, so an admin can verify
// the endpoint + signature. A stub receiver captures the delivery and confirms the payload + signature.
[Collection(E2ECollection.Name)]
public class AuditWebhookTestDeliveryTests
{
    private readonly E2EApiFactory _factory;

    public AuditWebhookTestDeliveryTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Test_delivery_signs_and_sends_a_synthetic_event_to_the_saved_webhook()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var adminEmail = $"wh-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "wh-1234", "Webhook Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "wh-1234"));
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var name = (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("name").GetString();

        // Before a webhook is configured, a test is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/tenant-settings/audit-webhook/test", null)).StatusCode);

        // Stand up a stub SIEM receiver + configure the tenant's webhook to point at it.
        const string signingSecret = "test-signing-secret";
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var captured = CaptureOneAsync(listener);

        await TestJson.Put(admin, "/api/tenant-settings", new
        {
            name,
            defaultOcrLanguages = "eng",
            auditRetentionDays = 365,
            auditWebhookUrl = $"http://localhost:{port}/ingest",
            auditWebhookSecret = signingSecret,
        });

        // The test delivery succeeds.
        var result = await TestJson.Post(admin, "/api/tenant-settings/audit-webhook/test", new { });
        Assert.True(result.GetProperty("success").GetBoolean());

        // The receiver got the synthetic event, correctly HMAC-signed with the saved secret.
        var (body, signature) = await captured.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("\"Webhook.Test\"", body);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(body)));
        Assert.Equal(expected, signature);

        // A non-admin can't trigger a test delivery.
        Assert.Equal(HttpStatusCode.Forbidden, (await sa.PostAsync("/api/tenant-settings/audit-webhook/test", null)).StatusCode);
    }

    private static async Task<(string Body, string? Signature)> CaptureOneAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }
        var signature = context.Request.Headers["X-SimplArchive-Signature"];
        context.Response.StatusCode = 200;
        context.Response.Close();
        return (body, signature);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
